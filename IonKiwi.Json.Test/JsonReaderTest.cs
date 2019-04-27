using System;
using System.Globalization;
using System.IO;
using System.Text;
using Xunit;
using static IonKiwi.Json.JsonReader;

namespace IonKiwi.Json.Test {
	public class JsonReaderTest {
		[Fact]
		public void TestEmptyOject() {
			string json = "{}";

			var reader = new JsonReader(new Utf8ByteArrayInputReader(Encoding.UTF8.GetBytes(json)));
			Assert.Equal(0, reader.Depth);
			Assert.Equal(string.Empty, reader.GetPath());
			Assert.Equal(1, reader.CharacterPosition);
			Assert.Equal(1, reader.LineNumber);

			var token = reader.ReadSync();
			Assert.Equal(JsonToken.ObjectStart, token);
			Assert.Equal(1, reader.Depth);
			Assert.Equal(string.Empty, reader.GetPath());
			Assert.Equal(2, reader.CharacterPosition);
			Assert.Equal(1, reader.LineNumber);

			token = reader.ReadSync();
			Assert.Equal(JsonToken.ObjectEnd, token);
			Assert.Equal(0, reader.Depth);
			Assert.Equal(string.Empty, reader.GetPath());
			Assert.Equal(3, reader.CharacterPosition);
			Assert.Equal(1, reader.LineNumber);

			token = reader.ReadSync();
			Assert.Equal(JsonToken.None, token);
			Assert.Equal(0, reader.Depth);
			Assert.Equal(string.Empty, reader.GetPath());
			Assert.Equal(3, reader.CharacterPosition);
			Assert.Equal(1, reader.LineNumber);

			return;
		}

		[Fact]
		public void CreateTest() {
			byte[] json = Helper.GetStringData("Object1.json");

			var reader = new JsonReader(new Utf8ByteArrayInputReader(json));

			StringBuilder sb = new StringBuilder();
			sb.AppendLine("JsonToken token;");
			sb.AppendLine("var reader = new JsonReader(new Utf8ByteArrayInputReader(Encoding.UTF8.GetBytes(json)));");
			sb.AppendLine("Assert.Equal(0, reader.Depth);");
			sb.AppendLine("Assert.Equal(string.Empty, reader.GetPath());");
			sb.AppendLine("Assert.Equal(1, reader.CharacterPosition);");
			sb.AppendLine("Assert.Equal(1, reader.LineNumber);");

			do {
				var token = reader.ReadSync();
				sb.AppendLine();
				sb.AppendLine("token = reader.ReadSync();");
				sb.AppendLine("Assert.Equal(JsonToken." + token.ToString() + ", token);");
				sb.AppendLine("Assert.Equal(" + reader.Depth.ToString(CultureInfo.InvariantCulture) + ", reader.Depth);");
				sb.AppendLine("Assert.Equal(\"" + reader.GetPath().Replace("\"", "\\\"") + "\", reader.GetPath());");
				sb.AppendLine("Assert.Equal(" + reader.CharacterPosition.ToString(CultureInfo.InvariantCulture) + ", reader.CharacterPosition);");
				sb.AppendLine("Assert.Equal(" + reader.LineNumber.ToString(CultureInfo.InvariantCulture) + ", reader.LineNumber);");

				if (token == JsonToken.None) {
					break;
				}
			}
			while (true);

			string testCode = sb.ToString();
			return;
		}
	}
}
