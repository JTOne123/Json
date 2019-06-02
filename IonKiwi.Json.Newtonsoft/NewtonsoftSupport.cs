﻿using IonKiwi.Json.MetaData;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization;

namespace IonKiwi.Json.Newtonsoft {
	public static class NewtonsoftSupport {
		public static void Register() {
			JsonMetaData.MetaData += (sender, e) => {
				var objectAttr = e.RootType.GetCustomAttribute<global::Newtonsoft.Json.JsonObjectAttribute>();
				var arrayAttr = e.RootType.GetCustomAttribute<global::Newtonsoft.Json.JsonArrayAttribute>();
				var dictAttr = e.RootType.GetCustomAttribute<global::Newtonsoft.Json.JsonDictionaryAttribute>();

				var typeHierarchy = new List<Type>() { e.RootType };
				var parentType = e.RootType.BaseType;
				while (parentType != null) {
					if (parentType == typeof(object) || parentType == typeof(ValueType)) {
						break;
					}
					typeHierarchy.Add(parentType);
					parentType = parentType.BaseType;
				}

				for (int i = typeHierarchy.Count - 1; i >= 0; i--) {
					var currentType = typeHierarchy[i];
					var m = currentType.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
					foreach (MethodInfo mi in m) {
						var l2 = mi.GetCustomAttributes(typeof(OnDeserializingAttribute), false);
						if (l2 != null && l2.Length > 0) {
							e.AddOnDeserializing(CreateCallbackAction(mi));
						}

						var l1 = mi.GetCustomAttributes(typeof(OnDeserializedAttribute), false);
						if (l1 != null && l1.Length > 0) {
							e.AddOnDeserialized(CreateCallbackAction(mi));
						}
					}
				}

				if (dictAttr != null) {
					e.IsDictionary(new JsonDictionaryAttribute() {

					});
				}
				else if (arrayAttr != null) {
					e.IsCollection(new JsonCollectionAttribute() {

					});
				}
				else if (objectAttr != null) {
					e.IsObject(new JsonObjectAttribute() {

					});

					foreach (var p in e.RootType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
						var ignoreAttr = p.GetCustomAttribute<global::Newtonsoft.Json.JsonIgnoreAttribute>();
						var propAttr = p.GetCustomAttribute<global::Newtonsoft.Json.JsonPropertyAttribute>();
						var reqAttr = p.GetCustomAttribute<global::Newtonsoft.Json.JsonRequiredAttribute>();
						if (ignoreAttr != null) {
							continue;
						}
						else if (propAttr != null || reqAttr != null) {
							e.AddProperty(string.IsNullOrEmpty(propAttr?.PropertyName) ? p.Name : propAttr.PropertyName, p,
								required: reqAttr != null || propAttr?.Required == global::Newtonsoft.Json.Required.AllowNull || propAttr?.Required == global::Newtonsoft.Json.Required.Always);
						}
					}
					foreach (var f in e.RootType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
						var ignoreAttr = f.GetCustomAttribute<global::Newtonsoft.Json.JsonIgnoreAttribute>();
						var propAttr = f.GetCustomAttribute<global::Newtonsoft.Json.JsonPropertyAttribute>();
						var reqAttr = f.GetCustomAttribute<global::Newtonsoft.Json.JsonRequiredAttribute>();
						if (ignoreAttr != null) {
							continue;
						}
						else if (propAttr != null || reqAttr != null) {
							e.AddField(string.IsNullOrEmpty(propAttr?.PropertyName) ? f.Name : propAttr.PropertyName, f,
								required: reqAttr != null || propAttr?.Required == global::Newtonsoft.Json.Required.AllowNull || propAttr?.Required == global::Newtonsoft.Json.Required.Always);
						}
					}
				}
			};
		}

		private static Action<object> CreateCallbackAction(MethodInfo mi) {
			var p1 = Expression.Parameter(typeof(object), "p1");
			var e1 = Expression.Convert(p1, mi.DeclaringType);
			var methodCall = Expression.Call(e1, mi, Expression.New(typeof(StreamingContext)));
			var methodExpression = Expression.Lambda(methodCall, p1);
			var methodLambda = (Expression<Action<object>>)methodExpression;
			return methodLambda.Compile();
		}
	}
}