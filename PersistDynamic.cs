using OsLib;
using JsonPit;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Dynamic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

/// <summary>
/// For now it looks like Jil can not completely replace a templated version of JsonFile/JsonItem with a dynamic version
/// So far we have to try to do a parallel implementation with JsonFileDynamic/JsonItemDynamic
/// use ItemsBase, maybe even extend it => JsonItemsDynamic
/// ALT
/// Try to derive JsonItemDynamic from JsonItem and see if that works since Jil is supposed to be able to deal with inheritance
/// ALTALT
/// reimplement everything (JsonFile, JsonItem, JsonItems) with object and/or dynamic => throw away type safety (??!?)
/// </summary>

namespace Persist
{
	public static class DynamicExtensions
	{
		/// <summary>
		/// creates an ExpandoObject; eliminates all methods and all non-public data
		/// </summary>
		/// <param name="value">object of any type</param>
		/// <returns></returns>
		public static dynamic ToDynamic(this object value)
		{
			IDictionary<string, object> expando = new ExpandoObject();
			foreach (PropertyDescriptor property in TypeDescriptor.GetProperties(value.GetType()))
				expando.Add(property.Name, property.GetValue(value));
			return expando as ExpandoObject;
		}
		/// <summary>
		/// Extends a dynamic
		/// </summary>
		/// <param name="dyn"></param>
		/// <param name="dynamicPartAsJson"></param>
		/// <returns></returns>
		//public static dynamic ExtendDynamicBy(dynamic dyn, string dynamicPartAsJson)
		//{
		//	var dynString = JSON.SerializeDynamic(dyn);
		//	var extendedString = dynString.Substring(0, dynString.Length - 1) + "," + dynamicPartAsJson.Substring(1, dynamicPartAsJson.Length - 1);
		//	return JSON.DeserializeDynamic(extendedString);
		//}
		/// <summary>
		/// Entends everything except a dynamic
		/// </summar
		/// <typeparam name="T"></typeparam>
		/// <param name="value"></param>
		/// <param name="dynamicPartAsJson"></param>
		/// <returns></returns>
		public static dynamic ExtendBy<T>(this T value, string dynamicPartAsJson)
		{
			IDictionary<string, object> expando = new ExpandoObject();
			foreach (PropertyDescriptor property in TypeDescriptor.GetProperties(value.GetType()))
				expando.Add(property.Name, property.GetValue(value));
			var dict = JObject.Parse(dynamicPartAsJson);
			//dynamic dynObj = JSON.DeserializeDynamic(dynamicPartAsJson) as ExpandoObject;
			//var expandoDict = dynObj as IDictionary<string, object>;
			foreach (var kv in dict)
				expando.Add(kv.Key, kv.Value);
			return expando as ExpandoObject;
		}
	}
	/// <summary>
	/// Adds dynamic object behavior to a JsonItem
	/// </summary>
	public class MyDynamicItem : Item
	{
		/// <summary>
		/// directly accessible dynamic for setter getter access
		/// </summary>
		public dynamic d;
		/// <summary>
		/// creates a dynamic that contains the properties of the static and dynamic part of the DynamicItem
		/// </summary>
		/// <remarks>the static properties are starting with an uppercase letter</remarks>
		/// <returns>a new dynamic object - just fields and properties, no methods</returns>
		public override dynamic Clone()
		{
			//dynamic expando = new ExpandoObject();
			#region get the d properties
			IDictionary<string, object> expando = new ExpandoObject();
			foreach (var dProperty in d)
				expando.Add(dProperty.Key, dProperty.Value);
			#endregion
			#region add the known Properties of JsonItem that we also need
			expando[nameof(Modified)] = Modified;
			expando[nameof(Name)] = Name;
			expando[nameof(Deleted)] = Deleted;
			expando[nameof(Note)] = Note;
			#endregion
			return expando as ExpandoObject;
		}
		public MyDynamicItem(dynamic from, bool invalidate = false)
			: base()
		{
			d = from;
			try
			{
				Modified = (DateTimeOffset)from.Modified;
				Name = (string)from.Name;
				Deleted = (bool)from.Deleted;
				Note = (string)from.Note;
			}
			catch (Exception ex)
			{
				throw new Exception($"{nameof(MyDynamicItem)} constructor didn't find a property in the passed-in dynamic", ex);
			}
			if (invalidate)
				Invalidate();
		}
		public MyDynamicItem(string name, string comment, bool invalidate = true, string dynamicPartAsJson = "")
			: base(name, comment, invalidate)
		{
			if (!string.IsNullOrEmpty(dynamicPartAsJson))
				d = new MyDynamicItem(dynamicPartAsJson).d;
		}
		public MyDynamicItem(string dynamicPartAsJson)
		{
			dynamic obj = JObject.Parse(dynamicPartAsJson);
			Name = obj.Name ?? Guid.NewGuid().ToString();
			Note = obj.Note ?? "";
			#region parse obj.Deleted into Deleted
			{
				var deleted = false;
				var deletedString = (string)(obj.Deleted);
				if (!string.IsNullOrEmpty(deletedString))
					bool.TryParse(deletedString, out deleted);
				Deleted = deleted;
			}
			#endregion
			Modified = obj.Modified == null ? DateTimeOffset.UtcNow : DateTimeOffset.Parse((string)obj.Modified);
			d = obj;
		}
		public MyDynamicItem()
		{
		}
	}
	//	public class DynamicArrayHisto : JsonItem
	//	{
	//		/// <summary>
	//		/// directly accessible dynamic for setter getter access
	//		/// </summary>
	//		public Dictionary<string, History<JsonItem>> dict = new Dictionary<string, History<JsonItem>>();
	//		internal int Changes { get; set; }
	//		public dynamic this[string key]
	//		{
	//			get
	//			{
	//				return dict[key].Top()
	//			}
	//			set
	//			{
	//				#region quick insert if collection still empty
	//				if (dict.Count == 0)
	//				{
	//					dict[key] = value;
	//					Changes++;
	//					return true; // => invalidate
	//				}
	//				#endregion
	//				#region any incoming value could be outdated already => check all historic values for equality
	//				var q = (
	//					from x in Values
	//					where item.Time == x.Time && item.Value.CompareTo(x.Value) == 0 // same value AND same timestamp
	//			select x);
	//				#endregion
	//				#region add new value only if it's time/value combination is different from all stored historic values plus the latest value is different from the new one to insert
	//				if (q.Count() == 0 && item.Value.CompareTo(Values[0].Value) != 0)   // different value, no matter which timestamp
	//				{
	//					Values.Insert(0, item);
	//					Values = Values.OrderByDescending(x => x.Time).Take(MaxHistory).ToList();
	//					// Invalidate(); let the caller handle it
	//					Changes++;
	//					return true;
	//				}
	//				#endregion
	//				return false;
	//			}
	//		}
	//		/// <summary>
	//		/// creates a dynamic that contains the properties of the static and dynamic part of the DynamicItem
	//		/// </summary>
	//		/// <remarks>the static properties are starting with an uppercase letter</remarks>
	//		/// <returns>a new dynamic object - just fields and properties, no methods</returns>
	//		public override dynamic Clone()
	//		{
	//			//dynamic expando = new ExpandoObject();
	//			#region get the d properties
	//			IDictionary<string, object> expando = new ExpandoObject();
	//			foreach (var dProperty in d)
	//				expando.Add(dProperty.Key, dProperty.Value);
	//			#endregion
	//			#region add the known Properties of JsonItem that we also need
	//			expando[nameof(Modified)] = Modified;
	//			expando[nameof(Name)] = Name;
	//			expando[nameof(Deleted)] = Deleted;
	//			expando[nameof(Note)] = Note;
	//			#endregion
	//			return expando as ExpandoObject;
	//		}
	//		public DynamicItem(dynamic from, bool invalidate = false)
	//			: base()
	//		{
	//			d = from;
	//			try
	//			{
	//				Modified = (DateTimeOffset)from.Modified;
	//				Name = (string)from.Name;
	//				Deleted = (bool)from.Deleted;
	//				Note = (string)from.Note;
	//			}
	//			catch (Exception ex)
	//			{
	//				throw new Exception($"{nameof(DynamicItem)} constructor didn't find a property in the passed-in dynamic", ex);
	//			}
	//			if (invalidate)
	//				Invalidate();
	//		}
	//		/// <summary>
	//		/// initializes the static and the dynamic part (via JSON)
	//		/// </summary>
	//		/// <param name="name">the identifying name of the object</param>
	//		/// <param name="comment">any kind of note</param>
	//		/// <param name="invalidate">perform invalidate during initialization</param>
	//		/// <param name="dynamicPartAsJson">optional: fills the dynamic part of the DynamicItem with the deserialized object defined by this JSON parameter</param>
	//		public DynamicItem(string name, string comment, bool invalidate = true, string dynamicPartAsJson = null)
	//		: base(name, comment, invalidate)
	//	{
	//		d = string.IsNullOrEmpty(dynamicPartAsJson) ? new ExpandoObject() : JSON.DeserializeDynamic(dynamicPartAsJson);
	//	}
	//	public DynamicItem(string dynamicPartAsJson)
	//	{
	//		dynamic obj = JSON.DeserializeDynamic(dynamicPartAsJson);
	//		Name = obj.Name ?? Guid.NewGuid().ToString();
	//		Note = obj.Note ?? "";
	//		#region parse obj.Deleted into Deleted
	//		{
	//			var deleted = false;
	//			var deletedString = (string)(obj.Deleted);
	//			if (!string.IsNullOrEmpty(deletedString))
	//				bool.TryParse(deletedString, out deleted);
	//			Deleted = deleted;
	//		}
	//		#endregion
	//		Modified = obj.Modified == null ? DateTimeOffset.UtcNow : DateTimeOffset.Parse((string)obj.Modified);
	//		d = obj;
	//	}
	//	public DynamicItem()
	//	{
	//		d = new ExpandoObject();
	//	}
	//	public DynamicItem(JObject obj)
	//	{
	//		foreach (JProperty x in (JToken)obj)
	//		{ // if 'obj' is a JObject
	//			string name = x.Name;
	//			JToken value = x.Value;
	//		}
	//		//Name = (string)jToken[nameof(Name)];
	//		//Note = (string)jToken[nameof(Note)];
	//		//Modified = (DateTimeOffset)jToken[nameof(Modified)];
	//		//Deleted = (bool)jToken[nameof(Deleted)];
	//	}
	//	public DynamicItem(dynamic obj)
	//	{
	//		Name = obj.Name ?? Guid.NewGuid().ToString();
	//		Note = obj.Note ?? "";
	//		Deleted = obj.Deleted ?? false;
	//		Modified = obj.Modified == null ? DateTimeOffset.UtcNow : DateTimeOffset.Parse((string)obj.Modified);
	//		d = obj;
	//	}
	//}
	public static class MyDynamicItems
	{
		// TODO needs to be someplace else
		//public static dynamic Clone(object)
		/// <summary>Add property to a dynamic</summary>
		/// <param name="expando"></param>
		/// <param name="propertyName"></param>
		/// <param name="propertyValue"></param>
		/// <remarks>Excerpt From: Jay Hilyard, Stephen Teilhet. “C# 6.0 Cookbook.” iBooks. </remarks>
		public static void AddProperty(ExpandoObject expando, string propertyName, object propertyValue)
		{
			// ExpandoObject supports IDictionary so we can extend it like this
			var expandoDict = expando as IDictionary<string, object>;
			if (expandoDict.ContainsKey(propertyName))
				expandoDict[propertyName] = propertyValue;
			else
				expandoDict.Add(propertyName, propertyValue);
		}
		public static void AddEvent(ExpandoObject expando, string eventName, Action<object, EventArgs> handler)
		{
			var expandoDict = expando as IDictionary<string, object>;
			if (expandoDict.ContainsKey(eventName))
				expandoDict[eventName] = handler;
			else
				expandoDict.Add(eventName, handler);
		}
	}
}
