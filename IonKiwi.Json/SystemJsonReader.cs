﻿#region License
// Copyright (c) 2019 Ewout van der Linden
// https://github.com/IonKiwi/Json/blob/master/LICENSE
#endregion

#if NETCOREAPP3_0
using IonKiwi.Extenions;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace IonKiwi.Json {
	public class SystemJsonReader : IJsonReader, IDisposable {

		private readonly Stream _stream;
		private readonly Stack<StackInformation> _stack = new Stack<StackInformation>();
		private Stack<StackInformation> _rewindState = null;
		private JsonReader.JsonToken _token = JsonReader.JsonToken.None;
		private JsonReaderState _readerState = new JsonReaderState(new JsonReaderOptions() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Allow });
		private byte[] _buffer;
		private int _offset;
		private int _length = 0;
		private string _data = null;
		private int _bufferSize = 0x400;
		private bool _complete = false;

		public SystemJsonReader(Stream stream) {
			_stream = stream;
		}

		public JsonReader.JsonToken Token => _token;

		public int Depth {
			get {
				var c = _stack.Count;
				var offset = 0;
				if (c > 0) {
					var state = _stack.Peek();
					if (state.Token == JsonReader.JsonToken.ArrayEnd || state.Token == JsonReader.JsonToken.ObjectEnd) {
						offset = 1;
					}
					else if (state.Token == JsonReader.JsonToken.Comment && c > 1) {
						using (var e = _stack.GetEnumerator()) {
							e.MoveNext();
							e.MoveNext();
							if (e.Current.Token == JsonReader.JsonToken.ArrayStart) {
								offset = -1;
							}
							else if (c > 2) {
								e.MoveNext();
								if (e.Current.Token == JsonReader.JsonToken.None || e.Current.Token == JsonReader.JsonToken.ObjectProperty) {
									offset = 1;
								}
							}
						}
					}
				}
				return c - offset;
			}
		}

		public string GetValue() {
			return _data;
		}

		public JsonReader.JsonToken Read() {
			if (_rewindState != null) {
				return ReplayState();
			}
			else if (_complete) {
				return JsonReader.JsonToken.None;
			}
			else if (_offset >= _length) {
				EnsureBuffer();
				_offset = 0;
				_length = _stream.Read(_buffer);
			}

			JsonReader.JsonToken token;
			while (!ReadCore(new ReadOnlySpan<byte>(_buffer, _offset, _length - _offset), _length == 0, out token)) {
				int bytesInBuffer = _length - _offset;
				if (bytesInBuffer == 0) {
					// read more data
					_length = _stream.Read(_buffer);
					_offset = 0;
				}
				else if ((uint)bytesInBuffer > ((uint)_bufferSize / 2)) {
					// expand buffer
					_bufferSize = (_bufferSize < (int.MaxValue / 2)) ? _bufferSize * 2 : int.MaxValue;
					var buffer2 = ArrayPool<byte>.Shared.Rent(_bufferSize);

					// copy the unprocessed data
					Buffer.BlockCopy(_buffer, _offset, buffer2, 0, bytesInBuffer);

					ArrayPool<byte>.Shared.Return(_buffer);
					_buffer = buffer2;

					// read more data
					_length = _stream.Read(_buffer.AsSpan(bytesInBuffer));
					if (_length == 0) {
						ThrowMoreDataExpectedException();
					}
					_length += bytesInBuffer;
					_offset = 0;
				}
				else {
					Buffer.BlockCopy(_buffer, _offset, _buffer, 0, bytesInBuffer);

					// read more data
					_length = _stream.Read(_buffer.AsSpan(bytesInBuffer));
					if (_length == 0) {
						ThrowMoreDataExpectedException();
					}
					_length += bytesInBuffer;
					_offset = 0;
				}
			}
			return token;
		}

		public async ValueTask<JsonReader.JsonToken> ReadAsync() {
			if (_rewindState != null) {
				return ReplayState();
			}
			else if (_complete) {
				return JsonReader.JsonToken.None;
			}
			else if (_offset >= _length) {
				EnsureBuffer();
				_offset = 0;
				_length = await _stream.ReadAsync(_buffer.AsMemory()).ConfigureAwait(false);
			}

			JsonReader.JsonToken token;
			while (!ReadCore(new ReadOnlySpan<byte>(_buffer, _offset, _length - _offset), _length == 0, out token)) {
				int bytesInBuffer = _length - _offset;
				if (bytesInBuffer == 0) {
					// read more data
					_length = await _stream.ReadAsync(_buffer.AsMemory()).ConfigureAwait(false);
					_offset = 0;
				}
				else if ((uint)bytesInBuffer > ((uint)_bufferSize / 2)) {
					// expand buffer
					_bufferSize = (_bufferSize < (int.MaxValue / 2)) ? _bufferSize * 2 : int.MaxValue;
					var buffer2 = ArrayPool<byte>.Shared.Rent(_bufferSize);

					// copy the unprocessed data
					Buffer.BlockCopy(_buffer, _offset, buffer2, 0, bytesInBuffer);

					ArrayPool<byte>.Shared.Return(_buffer);
					_buffer = buffer2;

					// read more data
					_length = await _stream.ReadAsync(_buffer.AsMemory(bytesInBuffer)).ConfigureAwait(false);
					if (_length == 0) {
						ThrowMoreDataExpectedException();
					}
					_length += bytesInBuffer;
					_offset = 0;
				}
				else {
					// copy the unprocessed data
					Buffer.BlockCopy(_buffer, _offset, _buffer, 0, bytesInBuffer);

					// read more data
					_length = await _stream.ReadAsync(_buffer.AsMemory(bytesInBuffer)).ConfigureAwait(false);
					if (_length == 0) {
						ThrowMoreDataExpectedException();
					}
					_length += bytesInBuffer;
					_offset = 0;
				}
			}
			return token;
		}

		public void Read(Func<JsonReader.JsonToken, bool> callback) {
			JsonReader.JsonToken token;
			if (_rewindState != null) {
				bool cb;
				do {
					token = ReplayState();
					cb = callback(token);
				}
				while (_rewindState != null && cb);
				if (!cb) { return; }
			}
			else if (_complete) {
				callback(JsonReader.JsonToken.None);
				return;
			}

			if (_offset >= _length) {
				EnsureBuffer();
				_offset = 0;
				_length = _stream.Read(_buffer);
			}

			var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(_buffer, _offset, _length - _offset), _length == 0, _readerState);
			do {
				while (!ReadCore(ref reader, reader.IsFinalBlock, out token)) {
					_offset += checked((int)reader.BytesConsumed);
					_readerState = reader.CurrentState;

					int bytesInBuffer = _length - _offset;
					if (bytesInBuffer == 0) {
						// read more data
						_length = _stream.Read(_buffer);
						_offset = 0;
						reader = new Utf8JsonReader(new ReadOnlySpan<byte>(_buffer, 0, _length), _length == 0, _readerState);
					}
					else if ((uint)bytesInBuffer > ((uint)_bufferSize / 2)) {
						// expand buffer
						_bufferSize = (_bufferSize < (int.MaxValue / 2)) ? _bufferSize * 2 : int.MaxValue;
						var buffer2 = ArrayPool<byte>.Shared.Rent(_bufferSize);

						// copy the unprocessed data
						Buffer.BlockCopy(_buffer, _offset, buffer2, 0, bytesInBuffer);

						ArrayPool<byte>.Shared.Return(_buffer);
						_buffer = buffer2;

						// read more data
						_length = _stream.Read(_buffer.AsSpan(bytesInBuffer));
						if (_length == 0) {
							ThrowMoreDataExpectedException();
						}
						_length += bytesInBuffer;
						_offset = 0;
						reader = new Utf8JsonReader(new ReadOnlySpan<byte>(_buffer, 0, _length), _length == 0, _readerState);
					}
					else {
						Buffer.BlockCopy(_buffer, _offset, _buffer, 0, bytesInBuffer);

						// read more data
						_length = _stream.Read(_buffer.AsSpan(bytesInBuffer));
						if (_length == 0) {
							ThrowMoreDataExpectedException();
						}
						_length += bytesInBuffer;
						_offset = 0;
						reader = new Utf8JsonReader(new ReadOnlySpan<byte>(_buffer, 0, _length), _length == 0, _readerState);
					}
				}
			}
			while (callback(token));
			_offset += checked((int)reader.BytesConsumed);
			_readerState = reader.CurrentState;
		}

		public async ValueTask ReadAsync(Func<JsonReader.JsonToken, ValueTask<bool>> callback) {
			bool cb;
			JsonReader.JsonToken token;
			if (_rewindState != null) {
				do {
					token = ReplayState();
					cb = await callback(token).NoSync();
				}
				while (_rewindState != null && cb);
				if (!cb) { return; }
			}
			else if (_complete) {
				await callback(JsonReader.JsonToken.None).NoSync();
				return;
			}

			if (_offset >= _length) {
				EnsureBuffer();
				_offset = 0;
				_length = await _stream.ReadAsync(_buffer.AsMemory()).ConfigureAwait(false);
			}

			do {
				ValueTask<bool> continuation;
				while (!HandleDataBlock(callback, out continuation)) {
					int bytesInBuffer = _length - _offset;
					if (bytesInBuffer == 0) {
						// read more data
						_length = await _stream.ReadAsync(_buffer.AsMemory()).ConfigureAwait(false);
						_offset = 0;
					}
					else if ((uint)bytesInBuffer > ((uint)_bufferSize / 2)) {
						// expand buffer
						_bufferSize = (_bufferSize < (int.MaxValue / 2)) ? _bufferSize * 2 : int.MaxValue;
						var buffer2 = ArrayPool<byte>.Shared.Rent(_bufferSize);

						// copy the unprocessed data
						Buffer.BlockCopy(_buffer, _offset, buffer2, 0, bytesInBuffer);

						ArrayPool<byte>.Shared.Return(_buffer);
						_buffer = buffer2;

						// read more data
						_length = await _stream.ReadAsync(_buffer.AsMemory(bytesInBuffer)).ConfigureAwait(false);
						if (_length == 0) {
							ThrowMoreDataExpectedException();
						}
						_length += bytesInBuffer;
						_offset = 0;
					}
					else {
						// copy the unprocessed data
						Buffer.BlockCopy(_buffer, _offset, _buffer, 0, bytesInBuffer);

						// read more data
						_length = await _stream.ReadAsync(_buffer.AsMemory(bytesInBuffer)).ConfigureAwait(false);
						if (_length == 0) {
							ThrowMoreDataExpectedException();
						}
						_length += bytesInBuffer;
						_offset = 0;
					}
				}
				if (!continuation.IsCompletedSuccessfully) {
					cb = await continuation.NoSync();
				}
				else {
					cb = continuation.Result;
				}
			} while (cb);
		}

		private bool HandleDataBlock(Func<JsonReader.JsonToken, ValueTask<bool>> callback, out ValueTask<bool> continuation) {
			var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(_buffer, _offset, _length - _offset), _length == 0, _readerState);
			continuation = default;
			do {
				if (!ReadCore(ref reader, reader.IsFinalBlock, out var token)) {
					_offset += checked((int)reader.BytesConsumed);
					_readerState = reader.CurrentState;
					return false;
				}

				continuation = callback(token);
			}
			while (continuation.IsCompletedSuccessfully && continuation.Result);
			_offset += checked((int)reader.BytesConsumed);
			_readerState = reader.CurrentState;
			return true;
		}

		private JsonReader.JsonToken ReplayState() {
			var item = _rewindState.Pop();
			if (_rewindState.Count == 0) {
				_rewindState = null;
			}
			if (_stack.Peek().Token == JsonReader.JsonToken.Comment) {
				_stack.Pop();
			}
			_stack.Push(item);
			if (item.Token == JsonReader.JsonToken.None) {
				item = _rewindState.Pop();
				if (_rewindState.Count == 0) {
					_rewindState = null;
				}
				_stack.Push(item);
			}
			_token = item.Token;
			return _token;
		}

		private void EnsureBuffer() {
			if (_buffer == null) {
				_buffer = ArrayPool<byte>.Shared.Rent(_bufferSize);
			}
		}

		private void PrepareStack(JsonReader.JsonToken token) {

			if (_stack.Count > 0) {
				var prevToken = _stack.Peek().Token;
				if (prevToken == JsonReader.JsonToken.Comment) {
					_stack.Pop();
				}
			}

			if (token == JsonReader.JsonToken.Comment) {
				return;
			}

			if (_stack.Count > 0) {
				var prevToken = _stack.Peek();
				if (prevToken.Token == JsonReader.JsonToken.ArrayStart && token != JsonReader.JsonToken.ArrayEnd) {
					prevToken.ItemCount++;
					// array item
					_stack.Push(new StackInformation() { Token = JsonReader.JsonToken.None, Data = null });
				}
			}

			while (_stack.Count > 0) {
				var prevToken = _stack.Peek().Token;
				if (JsonReader.IsValueToken(prevToken)) {
					_stack.Pop();
					prevToken = _stack.Count > 0 ? _stack.Peek().Token : JsonReader.JsonToken.None;
					if (_stack.Count > 1 && (prevToken == JsonReader.JsonToken.ObjectProperty || prevToken == JsonReader.JsonToken.None)) {
						_stack.Pop();
					}
				}
				else if (prevToken == JsonReader.JsonToken.ObjectEnd || prevToken == JsonReader.JsonToken.ArrayEnd) {
					_stack.Pop();
					_stack.Pop();
					prevToken = _stack.Count > 0 ? _stack.Peek().Token : JsonReader.JsonToken.None;
					if (_stack.Count > 1 && (prevToken == JsonReader.JsonToken.ObjectProperty || prevToken == JsonReader.JsonToken.None)) {
						_stack.Pop();
					}
				}
				else {
					break;
				}
			}

			if (_stack.Count > 0) {
				var prevToken = _stack.Peek();
				if (prevToken.Token == JsonReader.JsonToken.ArrayStart && token != JsonReader.JsonToken.ArrayEnd) {
					prevToken.ItemCount++;
					// array item
					_stack.Push(new StackInformation() { Token = JsonReader.JsonToken.None, Data = null });
				}
			}
		}

		private bool ReadCore(ReadOnlySpan<byte> bytes, bool isFinalBlock, out JsonReader.JsonToken token) {
			var reader = new Utf8JsonReader(bytes, isFinalBlock, _readerState);
			var result = ReadCore(ref reader, isFinalBlock, out token);
			_offset += checked((int)reader.BytesConsumed);
			_readerState = reader.CurrentState;
			return result;
		}

		private bool ReadCore(ref Utf8JsonReader reader, bool isFinalBlock, out JsonReader.JsonToken token) {
			var result = reader.Read();
			if (result) {
				token = FromSystemToken(reader.TokenType);
				PrepareStack(token);

				if (token == JsonReader.JsonToken.ObjectProperty) {
					_data = reader.GetString();
					_stack.Peek().ItemCount++;
				}
				else if (token == JsonReader.JsonToken.String) {
					_data = reader.GetString();
				}
				else if (token == JsonReader.JsonToken.Comment) {
					ReadOnlySpan<byte> span = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan;
					_data = Encoding.UTF8.GetString(span);
					if (_stack.Count > 0) {
						var prevToken = _stack.Peek();
						if (prevToken.Token == JsonReader.JsonToken.ObjectStart || prevToken.Token == JsonReader.JsonToken.ArrayStart) {
							if (prevToken.CommentsBeforeFirstToken == null) {
								prevToken.CommentsBeforeFirstToken = new List<string>();
							}
							prevToken.CommentsBeforeFirstToken.Add(_data);
						}
					}
				}
				else if (token == JsonReader.JsonToken.Number) {
					ReadOnlySpan<byte> span = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan;
					_data = Encoding.UTF8.GetString(span);
				}
				else if (token == JsonReader.JsonToken.Boolean) {
					var b = reader.GetBoolean();
					_data = b ? "true" : "false";
				}
				else if (token == JsonReader.JsonToken.Null) {
					_data = "null";
				}
				else {
					_data = null;
				}

				_stack.Push(new StackInformation() { Token = token, Data = _data });
			}
			else {
				token = JsonReader.JsonToken.None;
				if (isFinalBlock) {
					PrepareStack(token);
					result = true;
					_complete = true;
					if (_length > _offset + checked((int)reader.BytesConsumed)) {
						ThrowUnexpectedDataException();
					}
				}
			}
			_token = token;
			return result;
		}

		public string ReadRaw(JsonWriteMode writeMode = JsonWriteMode.Json) {
			if (writeMode != JsonWriteMode.Json) {
				ThrowNotSupportedException(writeMode.ToString());
			}

			var currentToken = Token;

			if (currentToken == JsonReader.JsonToken.Comment) {
				do {
					currentToken = Read();
				}
				while (currentToken == JsonReader.JsonToken.Comment);
			}

			if (currentToken == JsonReader.JsonToken.ObjectProperty) {
				currentToken = Read();
			}

			if (currentToken == JsonReader.JsonToken.Comment) {
				do {
					currentToken = Read();
				}
				while (currentToken == JsonReader.JsonToken.Comment);
			}

			if (JsonReader.IsValueToken(currentToken)) {
				if (currentToken == JsonReader.JsonToken.String) {
					using (var ms = new MemoryStream()) {
						using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions() { Indented = false })) {
							writer.WriteStringValue(GetValue());
						}
						return Encoding.UTF8.GetString(ms.ToArray());
					}
				}
				return GetValue();
			}
			else if (currentToken == JsonReader.JsonToken.ObjectStart || currentToken == JsonReader.JsonToken.ArrayStart) {

				var stack = new Stack<RawPosition>();
				stack.Push(new RawPosition());

				using (var ms = new MemoryStream()) {
					using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions() { Indented = false })) {

						WriteToken(writer, writeMode, currentToken, true);

						var startDepth = Depth;

						do {
							currentToken = Read();
							if (currentToken == JsonReader.JsonToken.Comment) {
								do {
									currentToken = Read();
								}
								while (currentToken == JsonReader.JsonToken.Comment);
							}

							var position = stack.Peek();
							switch (currentToken) {
								case JsonReader.JsonToken.None:
									ThrowMoreDataExpectedException();
									return null;
								case JsonReader.JsonToken.ObjectStart:
								case JsonReader.JsonToken.ArrayStart:
									stack.Push(new RawPosition());
									break;
								case JsonReader.JsonToken.ObjectProperty:
									stack.Push(new RawPosition() { IsProperty = true });
									break;
								case JsonReader.JsonToken.ObjectEnd:
								case JsonReader.JsonToken.ArrayEnd: {
										stack.Pop();
										if (stack.Count > 0) {
											position = stack.Peek();
											if (position.IsProperty) { stack.Pop(); }
										}
										break;
									}
							}
							WriteToken(writer, writeMode, currentToken, position.IsFirst);
							position.IsFirst = false;
							if (position.IsProperty && (currentToken == JsonReader.JsonToken.Boolean || currentToken == JsonReader.JsonToken.Null || currentToken == JsonReader.JsonToken.Number || currentToken == JsonReader.JsonToken.String)) {
								stack.Pop();
							}
						}
						while (Depth != startDepth);
					}
					return Encoding.UTF8.GetString(ms.ToArray());
				}
			}
			else {
				ThrowNotStartTag(currentToken);
				return null;
			}
		}

		public async ValueTask<string> ReadRawAsync(JsonWriteMode writeMode = JsonWriteMode.Json) {
			if (writeMode != JsonWriteMode.Json) {
				ThrowNotSupportedException(writeMode.ToString());
			}

			var currentToken = Token;

			if (currentToken == JsonReader.JsonToken.Comment) {
				do {
					currentToken = await ReadAsync().NoSync();
				}
				while (currentToken == JsonReader.JsonToken.Comment);
			}

			if (currentToken == JsonReader.JsonToken.ObjectProperty) {
				currentToken = await ReadAsync().NoSync();
			}

			if (currentToken == JsonReader.JsonToken.Comment) {
				do {
					currentToken = await ReadAsync().NoSync();
				}
				while (currentToken == JsonReader.JsonToken.Comment);
			}

			if (JsonReader.IsValueToken(currentToken)) {
				if (currentToken == JsonReader.JsonToken.String) {
					using (var ms = new MemoryStream()) {
						using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions() { Indented = false })) {
							writer.WriteStringValue(GetValue());
						}
						return Encoding.UTF8.GetString(ms.ToArray());
					}
				}
				return GetValue();
			}
			else if (currentToken == JsonReader.JsonToken.ObjectStart || currentToken == JsonReader.JsonToken.ArrayStart) {

				var stack = new Stack<RawPosition>();
				stack.Push(new RawPosition());

				using (var ms = new MemoryStream()) {
					using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions() { Indented = false })) {

						WriteToken(writer, writeMode, currentToken, true);

						var startDepth = Depth;

						do {
							currentToken = await ReadAsync().NoSync();
							if (currentToken == JsonReader.JsonToken.Comment) {
								do {
									currentToken = await ReadAsync().NoSync();
								}
								while (currentToken == JsonReader.JsonToken.Comment);
							}

							var position = stack.Peek();
							switch (currentToken) {
								case JsonReader.JsonToken.None:
									ThrowMoreDataExpectedException();
									return null;
								case JsonReader.JsonToken.ObjectStart:
								case JsonReader.JsonToken.ArrayStart:
									stack.Push(new RawPosition());
									break;
								case JsonReader.JsonToken.ObjectProperty:
									stack.Push(new RawPosition() { IsProperty = true });
									break;
								case JsonReader.JsonToken.ObjectEnd:
								case JsonReader.JsonToken.ArrayEnd: {
										stack.Pop();
										if (stack.Count > 0) {
											position = stack.Peek();
											if (position.IsProperty) { stack.Pop(); }
										}
										break;
									}
							}
							WriteToken(writer, writeMode, currentToken, position.IsFirst);
							position.IsFirst = false;
							if (position.IsProperty && (currentToken == JsonReader.JsonToken.Boolean || currentToken == JsonReader.JsonToken.Null || currentToken == JsonReader.JsonToken.Number || currentToken == JsonReader.JsonToken.String)) {
								stack.Pop();
							}
						}
						while (Depth != startDepth);
					}
					return Encoding.UTF8.GetString(ms.ToArray());
				}
			}
			else {
				ThrowNotStartTag(currentToken);
				return null;
			}
		}

		private void WriteToken(Utf8JsonWriter writer, JsonWriteMode writeMode, JsonReader.JsonToken token, bool isFirst) {
			switch (token) {
				case JsonReader.JsonToken.ObjectStart: {
						writer.WriteStartObject();
						break;
					}
				case JsonReader.JsonToken.ArrayStart: {
						writer.WriteStartArray();
						break;
					}
				case JsonReader.JsonToken.ObjectEnd:
					writer.WriteEndObject();
					break;
				case JsonReader.JsonToken.ArrayEnd:
					writer.WriteEndArray();
					break;
				case JsonReader.JsonToken.ObjectProperty: {
						writer.WritePropertyName(GetValue());
						break;
					}
				case JsonReader.JsonToken.Boolean:
					if (GetValue() == "true") {
						writer.WriteBooleanValue(true);
					}
					else {
						writer.WriteBooleanValue(false);
					}
					break;
				case JsonReader.JsonToken.Null:
					writer.WriteNullValue();
					break;
				case JsonReader.JsonToken.Number: {
						var v = GetValue();
						var pi = v.IndexOf('.');
						if (pi >= 0) {
							int digits = v.Length - pi - 1;
							if (digits > 15) {
								if (decimal.TryParse(v, NumberStyles.AllowLeadingSign | NumberStyles.AllowExponent | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var decimalValue)) {
									writer.WriteNumberValue(decimalValue);
								}
								else {
									ThrowInvalidJson(v);
								}
							}
							else {
								if (double.TryParse(v, NumberStyles.AllowLeadingSign | NumberStyles.AllowExponent | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var doubleValue)) {
									writer.WriteNumberValue(doubleValue);
								}
								else {
									ThrowInvalidJson(v);
								}
							}
						}
						else {
							bool negativeExponent = false;
							int e = v.IndexOf('e');
							if (e < 0) {
								e = v.IndexOf('E');
							}
							if (e >= 0) {
								if (v[e + 1] == '-') {
									negativeExponent = true;
								}
							}

							if (negativeExponent) {
								if (double.TryParse(v, NumberStyles.AllowLeadingSign | NumberStyles.AllowExponent | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var doubleValue)) {
									writer.WriteNumberValue(doubleValue);
								}
								else {
									ThrowInvalidJson(v);
								}
							}
							else {
								if (long.TryParse(v, NumberStyles.AllowLeadingSign | NumberStyles.AllowExponent | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var longValue)) {
									writer.WriteNumberValue(longValue);
								}
								else {
									ThrowInvalidJson(v);
								}
							}
						}
						break;
					}
				case JsonReader.JsonToken.String: {
						var v = GetValue();
						writer.WriteStringValue(v);
						break;
					}
				default:
					ThrowUnhandledToken(token);
					break;
			}
		}

		public async ValueTask SkipAsync() {
			var token = _token;
			if (token == JsonReader.JsonToken.Comment) {
				do {
					token = await ReadAsync().NoSync();
				}
				while (token == JsonReader.JsonToken.Comment);
			}

			if (JsonReader.IsValueToken(token)) {
				return;
			}
			else switch (token) {
					case JsonReader.JsonToken.ObjectStart: {
							var depth = Depth;
							do {
								token = await ReadAsync().NoSync();
								if (token == JsonReader.JsonToken.ObjectEnd && depth == Depth) {
									return;
								}
							}
							while (token != JsonReader.JsonToken.None);

							ThrowMoreDataExpectedException();
							break;
						}
					case JsonReader.JsonToken.ArrayStart: {
							var depth = Depth;
							do {
								token = await ReadAsync().NoSync();
								if (token == JsonReader.JsonToken.ArrayEnd && depth == Depth) {
									return;
								}
							}
							while (token != JsonReader.JsonToken.None);

							ThrowMoreDataExpectedException();
							break;
						}
					case JsonReader.JsonToken.ObjectProperty: {
							await ReadAsync().NoSync();
							token = _token;
							if (token == JsonReader.JsonToken.ObjectStart || token == JsonReader.JsonToken.ArrayStart) {
								await SkipAsync().NoSync();
							}
							break;
						}
					default:
						ThrowReaderNotSkippablePosition(_token);
						break;
				}
		}

		public void Skip() {
			var token = _token;
			if (token == JsonReader.JsonToken.Comment) {
				do {
					token = Read();
				}
				while (token == JsonReader.JsonToken.Comment);
			}

			if (JsonReader.IsValueToken(token)) {
			}
			else switch (token) {
					case JsonReader.JsonToken.ObjectStart: {
							var depth = Depth;
							do {
								token = Read();
								if (token == JsonReader.JsonToken.ObjectEnd && depth == Depth) {
									return;
								}
							}
							while (token != JsonReader.JsonToken.None);

							ThrowMoreDataExpectedException();
							break;
						}
					case JsonReader.JsonToken.ArrayStart: {
							var depth = Depth;
							do {
								token = Read();
								if (token == JsonReader.JsonToken.ArrayEnd && depth == Depth) {
									return;
								}
							}
							while (token != JsonReader.JsonToken.None);

							ThrowMoreDataExpectedException();
							break;
						}
					case JsonReader.JsonToken.ObjectProperty: {
							Read();
							token = _token;
							if (token == JsonReader.JsonToken.ObjectStart || token == JsonReader.JsonToken.ArrayStart) {
								Skip();
							}
							break;
						}
					default:
						ThrowReaderNotSkippablePosition(_token);
						break;
				}
		}

		private static void ThrowReaderNotSkippablePosition(JsonReader.JsonToken token) {
			throw new InvalidOperationException("Reader is not at a skippable position. token: " + token);
		}

		private static void ThrowMoreDataExpectedException() {
			throw new JsonReader.MoreDataExpectedException();
		}

		private static void ThrowUnexpectedDataException() {
			throw new JsonReader.UnexpectedDataException();
		}

		private static void ThrowInvalidJson(string json) {
			throw new NotSupportedException("Invalid json: " + json);
		}

		private static void ThrowUnhandledToken(JsonReader.JsonToken token) {
			throw new NotImplementedException(token.ToString());
		}

		private static void ThrowNotStartTag(JsonReader.JsonToken token) {
			throw new InvalidOperationException("Reader is not positioned on a start tag. token: " + token);
		}

		private static void ThrowNotSupportedException(string message) {
			throw new NotSupportedException(message);
		}

		private static void ThrowTokenShouldBeObjectStartOrArrayStart(JsonReader.JsonToken token) {
			throw new InvalidOperationException($"'{nameof(token)}' should be {JsonReader.JsonToken.ObjectStart} or {JsonReader.JsonToken.ArrayStart}");
		}

		private static void ThrowTokenShouldBeObjectStart(JsonReader.JsonToken token) {
			throw new InvalidOperationException($"'{nameof(token)}' should be {JsonReader.JsonToken.ObjectStart}");
		}

		private static void ThowInvalidPositionForResetReaderPositionForVisitor() {
			throw new InvalidOperationException("Reader is not at a valid position for ResetReaderPositionForVisitor()");
		}

		private static void ThrowTokenShouldBeArrayStart(JsonReader.JsonToken token) {
			throw new InvalidOperationException($"'{nameof(token)}' should be {JsonReader.JsonToken.ArrayStart}");
		}

		private static void ThrowInternalStateCorruption() {
			throw new InvalidOperationException("Internal state corruption");
		}

		void IJsonReader.RewindReaderPositionForVisitor(JsonReader.JsonToken token) {
			if (!(token == JsonReader.JsonToken.ObjectStart || token == JsonReader.JsonToken.ArrayStart)) {
				ThrowTokenShouldBeObjectStartOrArrayStart(token);
			}

			var resetState = new Stack<StackInformation>();
			var state = _stack.Peek();

			// account for empty array/object
			if (state.Token == JsonReader.JsonToken.ObjectEnd) {
				if (token != JsonReader.JsonToken.ObjectStart) {
					ThrowTokenShouldBeObjectStart(token);
				}

				resetState.Push(state);
				_stack.Pop();

				state = _stack.Peek();
				if (state.Token != JsonReader.JsonToken.ObjectStart) {
					ThrowInternalStateCorruption();
				}
				else if (state.ItemCount != 0) {
					ThowInvalidPositionForResetReaderPositionForVisitor();
				}

				if (state.CommentsBeforeFirstToken != null) {
					for (int i = state.CommentsBeforeFirstToken.Count - 1; i >= 0; i--) {
						var currentComment = state.CommentsBeforeFirstToken[i];
						resetState.Push(new StackInformation() { Token = JsonReader.JsonToken.Comment, Data = currentComment });
					}
				}

				_token = JsonReader.JsonToken.ObjectStart;
				_rewindState = resetState;
			}
			// account for sub object
			else if (state.Token == JsonReader.JsonToken.ArrayEnd) {
				if (token != JsonReader.JsonToken.ArrayStart) {
					ThrowTokenShouldBeArrayStart(token);
				}

				resetState.Push(state);
				_stack.Pop();

				state = _stack.Peek();
				if (state.Token != JsonReader.JsonToken.ArrayStart) {
					ThrowInternalStateCorruption();
				}
				else if (state.ItemCount != 0) {
					ThowInvalidPositionForResetReaderPositionForVisitor();
				}

				if (state.CommentsBeforeFirstToken != null) {
					for (int i = state.CommentsBeforeFirstToken.Count - 1; i >= 0; i--) {
						var currentComment = state.CommentsBeforeFirstToken[i];
						resetState.Push(new StackInformation() { Token = JsonReader.JsonToken.Comment, Data = currentComment });
					}
				}

				_token = JsonReader.JsonToken.ArrayStart;
				_rewindState = resetState;
			}
			// account for sub array
			else if (state.Token == JsonReader.JsonToken.ObjectStart) {

				resetState.Push(state);
				_stack.Pop();

				state = _stack.Peek();
				if (state.Token != JsonReader.JsonToken.None) {
					ThrowInternalStateCorruption();
				}

				resetState.Push(state);
				_stack.Pop();

				state = _stack.Peek();
				if (state.Token != JsonReader.JsonToken.ArrayStart) {
					ThrowInternalStateCorruption();
				}
				else if (state.ItemCount != 1) {
					ThowInvalidPositionForResetReaderPositionForVisitor();
				}

				if (state.CommentsBeforeFirstToken != null) {
					for (int i = state.CommentsBeforeFirstToken.Count - 1; i >= 0; i--) {
						var currentComment = state.CommentsBeforeFirstToken[i];
						resetState.Push(new StackInformation() { Token = JsonReader.JsonToken.Comment, Data = currentComment });
					}
				}

				_token = token;
				_rewindState = resetState;
			}
			else if (state.Token == JsonReader.JsonToken.ArrayStart) {
				if (token == JsonReader.JsonToken.ObjectStart) {
					// '{[' is not valid json
					ThrowTokenShouldBeArrayStart(token);
				}

				resetState.Push(state);
				_stack.Pop();

				state = _stack.Peek();
				if (state.Token != JsonReader.JsonToken.None) {
					ThrowInternalStateCorruption();
				}

				resetState.Push(state);
				_stack.Pop();

				state = _stack.Peek();
				if (state.Token != JsonReader.JsonToken.ArrayStart) {
					ThrowInternalStateCorruption();
				}
				else if (state.ItemCount != 1) {
					ThowInvalidPositionForResetReaderPositionForVisitor();
				}

				if (state.CommentsBeforeFirstToken != null) {
					for (int i = state.CommentsBeforeFirstToken.Count - 1; i >= 0; i--) {
						var currentComment = state.CommentsBeforeFirstToken[i];
						resetState.Push(new StackInformation() { Token = JsonReader.JsonToken.Comment, Data = currentComment });
					}
				}

				_token = token;
				_rewindState = resetState;
			}
			else if (state.Token == JsonReader.JsonToken.ObjectProperty) {
				if (token != JsonReader.JsonToken.ObjectStart) {
					ThrowTokenShouldBeObjectStart(token);
				}

				resetState.Push(state);
				_stack.Pop();

				state = _stack.Peek();
				if (state.Token != JsonReader.JsonToken.ObjectStart) {
					ThrowInternalStateCorruption();
				}
				else if (state.ItemCount != 1) {
					ThowInvalidPositionForResetReaderPositionForVisitor();
				}

				if (state.CommentsBeforeFirstToken != null) {
					for (int i = state.CommentsBeforeFirstToken.Count - 1; i >= 0; i--) {
						var currentComment = state.CommentsBeforeFirstToken[i];
						resetState.Push(new StackInformation() { Token = JsonReader.JsonToken.Comment, Data = currentComment });
					}
				}

				_token = token;
				_rewindState = resetState;
			}
			else {

				resetState.Push(state);
				_stack.Pop();

				state = _stack.Peek();
				if (state.Token == JsonReader.JsonToken.ObjectProperty) {
					if (token != JsonReader.JsonToken.ObjectStart) {
						ThrowTokenShouldBeObjectStart(token);
					}

					resetState.Push(state);
					_stack.Pop();

					state = _stack.Peek();
					if (state.Token != JsonReader.JsonToken.ObjectStart) {
						ThrowInternalStateCorruption();
					}
					else if (state.ItemCount != 1) {
						ThowInvalidPositionForResetReaderPositionForVisitor();
					}

					if (state.CommentsBeforeFirstToken != null) {
						for (int i = state.CommentsBeforeFirstToken.Count - 1; i >= 0; i--) {
							var currentComment = state.CommentsBeforeFirstToken[i];
							resetState.Push(new StackInformation() { Token = JsonReader.JsonToken.Comment, Data = currentComment });
						}
					}

					_token = token;
					_rewindState = resetState;
				}
				else if (state.Token == JsonReader.JsonToken.None) {
					if (token != JsonReader.JsonToken.ArrayStart) {
						ThrowTokenShouldBeArrayStart(token);
					}

					resetState.Push(state);
					_stack.Pop();

					state = _stack.Peek();
					if (state.Token != JsonReader.JsonToken.ArrayStart) {
						ThrowInternalStateCorruption();
					}
					else if (state.ItemCount != 1) {
						ThowInvalidPositionForResetReaderPositionForVisitor();
					}

					if (state.CommentsBeforeFirstToken != null) {
						for (int i = state.CommentsBeforeFirstToken.Count - 1; i >= 0; i--) {
							var currentComment = state.CommentsBeforeFirstToken[i];
							resetState.Push(new StackInformation() { Token = JsonReader.JsonToken.Comment, Data = currentComment });
						}
					}

					_token = token;
					_rewindState = resetState;
				}
				else {
					ThowInvalidPositionForResetReaderPositionForVisitor();
				}
			}
		}

		void IJsonReader.Unwind() {
			do {
				var item = _rewindState.Pop();
				if (item.Token != JsonReader.JsonToken.Comment) {
					_stack.Push(item);
				}
				_token = item.Token;
			}
			while (_rewindState.Count > 0);
			_rewindState = null;
		}

		private static JsonReader.JsonToken FromSystemToken(JsonTokenType token) {
			switch (token) {
				case JsonTokenType.Comment:
					return JsonReader.JsonToken.Comment;
				case JsonTokenType.EndArray:
					return JsonReader.JsonToken.ArrayEnd;
				case JsonTokenType.EndObject:
					return JsonReader.JsonToken.ObjectEnd;
				case JsonTokenType.False:
				case JsonTokenType.True:
					return JsonReader.JsonToken.Boolean;
				case JsonTokenType.None:
					return JsonReader.JsonToken.None;
				case JsonTokenType.Null:
					return JsonReader.JsonToken.Null;
				case JsonTokenType.Number:
					return JsonReader.JsonToken.Number;
				case JsonTokenType.PropertyName:
					return JsonReader.JsonToken.ObjectProperty;
				case JsonTokenType.StartArray:
					return JsonReader.JsonToken.ArrayStart;
				case JsonTokenType.StartObject:
					return JsonReader.JsonToken.ObjectStart;
				case JsonTokenType.String:
					return JsonReader.JsonToken.String;
				default:
					ThrowNewNotSupportedException(token);
					return JsonReader.JsonToken.None;
			}
		}

		private static void ThrowNewNotSupportedException(JsonTokenType token) {
			throw new NotSupportedException(token.ToString());
		}

		void IDisposable.Dispose() {
			if (_buffer != null) {
				ArrayPool<byte>.Shared.Return(_buffer);
				_buffer = null;
			}
		}

		[DebuggerDisplay("{Token}: {Data}")]
		private sealed class StackInformation {
			public JsonReader.JsonToken Token;
			public string Data;
			public int ItemCount;
			public List<string> CommentsBeforeFirstToken;
		}

		private sealed class RawPosition {
			public bool IsFirst = true;
			public bool IsProperty = false;
		}
	}
}
#endif
