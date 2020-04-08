using RaiUtils;
using OsLib;
using JsonPit;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading;
//using System.Web.Script.Serialization;
using System.Xml;
using System.Xml.Serialization;

// NEW ATTEMPT to fix some new issues, in particular too many files/copies of the main file
// Only the master server writes the main file (ie Users.aspx) - a flag file master.info can exist; if it doesn't any server can write to the XmlFile. However, if it does exist, 
// only the server mentioned there is allowed to write to the XmlFile. The flag file also contains a timestamp which contains the last time the XmlFile was saved.
// A second flag [MachineName].info file exists as soon as a server saves any changes. The changes will not be written back to the XmlFile but a ChangeFile will be created instead.
// The second flag file contains the name of the server (that it is named after) and a timestamp that contains the time that this server last loaded the XmlFile into it's memory.
// Many other servers can read the file from the very same location in the Dropbox (ie ~Dropbox/Config/3.6/Users.xml); every time they do the second flag file will be updated (or created if it hasn't existed yet).
// The master maintains a file that contains the name of the current master and a timestamp for the last save/merge (ie ~Dropbox/Config/3.6/Users/Master.xml)
//    => changing the master in the file by hand changes the master
// Every server creates and maintains a file named after the server and with the server's name and a timestamp in it when the file was last loaded (ie ~Dropbox/Config/3.6/Users/Pro1997.xml)
// The local server can read the Master.xml to see if the file on disk is newer than the last file loaded which is the file in memory
// When the local server changes a setting, a change file will be written to the ChangeDir (ie ~Dropbox/Config/3.6/Users/20141124xxxxx_Pro1997.xml)
// After the master has merged the changes into the main file, all change files older than 15 min will be deleted.
// When any other server opens the main file it also has to merge all existing change files but can never delete any. If a master is not working or the Synchronization fails the amount of 
//    change files will stack up and an operator has to fix the synchronization, make sure the Master ist working or switch to a different master (see Master.xml). Ideally, the amount of 
//    change files is always small. If the number of changes per 15 min tends to become high => reconsider
// Result: Remote changes come in eventually, local changes are always current. If a conflict between a remote change and a local change comes up, the conflict will be resolved 
//    eventually and the change with the newest timestamp will prevail. All Timestamps are taken in universal time and all servers are expected to have a time synchronizing service running
//    so that no server get an unfair advantage.

// LATEST ATTEMPT to get this solved
// see detailled description in HDitem\ImageServer\doc\SynchronizedSettings\index.html
// every machine has it's own copy in a seperate dropbox dir named after the machine, delta files will be written to all server subdirectories on create/update/delete
// delta files will be inserted into the settings file the next time a Mem<T>-constructor will be called on this machine

// NEW APPROACH - busted (RSB, 2014-02-20)
// the LastWriteTimeUtc (of the XmlFile) will be set to the modified date of the youngest XmlSetting
// executed in: Store() and only there!
// TODO check if Dropbox alters this timestamp during synchronization (otherwise try CreationTime)
// this approach creates conflicted copies if changes appear on more than one server

// TODO use http://www.nuget.org/packages/Newtonsoft.Json and switch from XML to JSON 
// JSON.NET "Convert[s] JSON to and from XML"); could use my pretty printer for display; has to be edited in a form anyway
// advantage: serializes Dictionaries directly (currently done via explicit tranformation to an array)
// editor: http://tomeko.net/software/JSONedit/

// TODO see if we can use Firebase in Save()/Store() to propagate changes to Firebase as master instance
// may start out with Users or Servers and see how much traffic this causes
// the solution would have a single point of failure in Firebase - think about doing it with LucidB instead
// LucidB: MVC5 application running on one of our servers, maybe even a small vServer instance
// would no need a lot of storage, RAM or computing power. Would also be a nice MVC5 example app => could even run on Azure.
// Would probably not even be too expensive on Azure.
// The local xml files could be stored on Load as backup plan - if the remote connection is dead, Load falls back to the local XML file.

/*	Release Notes Persistence
 * ...
 * Synchronization
 * XmlFile<T> supports synchronization via CloudDrive i.e. Dropbox.
 * 
 * see SyncFile.cs, idea 3 for a high traffic self-synchronization version of XmlFile.cs
 * 
 */

namespace Persist
{
	// moved to Persist.cs
	//public static class License
	//{
	//	public static bool Init()
	//	{
	//		var rc = Licensing.RegisterLicense("3694-e1JlZjozNjk0LE5hbWU6IkpnZW5DeSBQcm9qZWN0LCBJbmMuIixUeXBlOlRleHRJbmRpZSxIYXNoOmVLYnJFQzE4QTBhNk9BVEZlT1RiS2pmaldDazBGb1ZBNHJLUExjSm14S2d4NjYyVVRiTzRPSHBTZ0hqVFBRZnJRR2tHLzBKa3NjcnowcWlUa1RHZDNSakZHOU5DdEFHb3FvNUJhVVpqV01OMXgxanhFaWI3aEQzL0hnMkd0aEF3Mm95bkY2clZkRkZwUHV2SDhBNTljU1ZIejRCbno2SktnRVhKTk9hL3RCYz0sRXhwaXJ5OjIwMTctMDQtMTB9");
	//		new AppHost().Init();
	//		return rc;
	//	}
	//}
	/// <summary>
	/// deprecated - Short flag file to contain info about the Server and the last load date of the xml files that is flagged (usually in a subdirectory)
	/// </summary>
	//public class XmlFileInfo : TextFile
	//{
	//	internal void set(string server, DateTimeOffset date)
	//	{
	//		setServer(server);
	//		setLastTimeLoaded(date);
	//		Save();  // probably not necessary - can be saved on first write access
	//	}
	//	private void setServer(string val)
	//	{
	//		if (Lines.Count < 1)
	//			Append(val + "|");
	//		else
	//		{
	//			string s = val + "|";
	//			if (Lines[0].Contains("|"))
	//				s += Lines[0].Split(new char[] { '|' })[1];
	//			Lines[0] = s;
	//		}
	//		Save();
	//	}
	//	private void setLastTimeLoaded(DateTimeOffset val)
	//	{
	//		if (Lines.Count < 1 || !Lines[0].Contains("|"))
	//			Append("|" + val.ToString("o")); // isn't this an error?
	//		else
	//		{
	//			string[] array = Lines[0].Split(new char[] { '|' });
	//			Lines[0] = array[0] + "|" + val.ToUniversalTime().ToString("o");
	//		}
	//		Save();
	//	}
	//	public string Server
	//	{
	//		get
	//		{
	//			if (Lines.Count < 1)
	//				return null;
	//			return Lines[0].Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries)[0];
	//		}
	//		set
	//		{
	//			setServer(value);
	//			Save();
	//		}
	//	}
	//	/// <summary>
	//	/// last write access for master.info and last read access for all others
	//	/// </summary>
	//	public DateTimeOffset LatestSettingChanged
	//	{
	//		get
	//		{
	//			if (Lines.Count < 1 || (Lines[0].Contains("|") && Lines[0].Split(new char[] { '|' })[1].Length == 0))
	//				return DateTimeOffset.MinValue;
	//			return DateTimeOffset.Parse(Lines[0].Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries)[1]);
	//		}
	//		set
	//		{
	//			setLastTimeLoaded(value);
	//			//Save();
	//		}
	//	}
	//	public new void Save(bool backup = false)
	//	{
	//		base.Save(backup);
	//	}
	//	public XmlFileInfo(string changeDir, string name)
	//		: base(changeDir + new RaiFile(name).Name + ".info")
	//	{
	//		Read();
	//	}
	//}
	/// <summary>
	/// enables advanced synchronization via modified
	/// </summary>
	public class XmlSetting : ICloneable
	{	// hint: use props <Tab> <Tab> to generate a property like eg in ServerItem
		/// <summary>Identifying name</summary>
		[XmlAttribute(nameof(name))]
		public string name;
		[XmlIgnore]
		public string Name { get { return name; } }
		/// <summary>has to be set to DateTimeOffset.UtcNow explicitely</summary>
		[XmlAttribute(nameof(modified))]
		public DateTime modified;
		virtual public DateTimeOffset Modified()
		{
			return new DateTimeOffset(modified, TimeSpan.Zero);
		}
		[XmlAttribute(nameof(deleted))]
		public bool deleted;
		public bool Delete(string by = null, bool backDate100 = true)
		{
			if (!deleted)
			{
				deleted = true;
				if (backDate100) 
					modified = (DateTimeOffset.UtcNow - new TimeSpan(0, 0, 0, 100)).UtcDateTime;	// backdate delete for 100ms
				Invalidate(preserveTimestamp: backDate100);	// changes modified
				var s = $"[{Modified().ToUniversalTime().ToString("u")}] deleted";
				if (!string.IsNullOrEmpty(by))
					s += " by " + by;
				Note = s + ";\n" + Note;
			}
			return true;
		}
		/// <summary>
		/// means: XmlSetting was modified from the original state of the setting as it was once loaded from disk; 
		/// in a concurrent environment this does not necessarily mean that the current setting on disk has (still) an older value
		/// since the file could have been updated on any other machine and synchronized back to this machine.
		/// Use merge to get the youngest value - merge also adjusts the dirty flag accordingly.
		/// </summary>
		protected bool dirty;
		virtual public bool Valid() { return !dirty; }
		/// <summary>call to indicate that the memory representation of the XmlSettings&lt;T&gt; now equals the file representation</summary>
		virtual public void Validate() { dirty = false; }
		/// <summary>call to indicate that the memory representation of the XmlSettings&lt;T&gt; differs from the file representation</summary>
		/// <param name="preserveTimestamp">does not update modified (only sets the dirty flag) if true</param>
		virtual public void Invalidate(bool preserveTimestamp = false)
		{
			dirty = true;
			if (!preserveTimestamp)
				modified = DateTimeOffset.UtcNow.UtcDateTime;	// different from DateTime.UtcNow?
		}
		public string Note { get; set; }
		/// <summary>
		/// also called by the debugger but no breakpoints will be hit by this calls
		/// </summary>
		/// <returns></returns>
		public override string ToString() => JObject.FromObject(this).ToString();
		///// <summary>
		///// list of strings, seperated by + or blank, that show up at least once in the ToString() representation of the setting
		///// </summary>
		///// <remarks>ToString() can have overloaded implementations in derived settingclasses; standard implementation here is JSON
		///// Explanation of Wildcard Usage
		///// ==================
		///// a filter can contain 0..1 wildcard sign (asterisk, '*')
		///// an asterisk in the beginning means: the field value or field name that ends with the given string is a match
		///// an asterisk in the end means: the field value or field name that starts with the given string is a match
		///// an asterisk in the middle means: the field value or field name that starts with the first part and ends with the last part is a match
		///// </remarks>
		///// <param name="setting"></param>
		///// <param name="filter">i.e. (different results, + is the conjunction operator): "Gravity":"C" or Gravity+C </param>
		///// <returns>true if condition(s) match</returns>
		//public bool Contains(string filter)
		//{
		//    if (string.IsNullOrWhiteSpace(filter))
		//        return true;
		//    string[] filterStrings = filter.Split(new char[] { '+', ' ' }, StringSplitOptions.RemoveEmptyEntries);
		//    string json = ToString();
		//    foreach (string filterString in filterStrings)
		//    {
		//        // wildcard resolution
		//        if (filterString.Contains('*'))
		//        {
		//            #region apply wildcard on value level -> works for field names and for quoted field values
		//            string[] keysAndValues = json.Split(new char[] { '{', '}', '"', ',', ':' }, StringSplitOptions.RemoveEmptyEntries);
		//            bool found = false;
		//            if (filterString.StartsWith("*"))
		//            {
		//                string toMatch = filterString.Substring(1);
		//                foreach (string word in keysAndValues)
		//                    if (word.Trim().EndsWith(toMatch))
		//                    {
		//                        found = true;
		//                        break;
		//                    }
		//            }
		//            else if (filterString.EndsWith("*"))
		//            {
		//                string toMatch = filterString.Substring(0, filterString.Length - 1);
		//                foreach (string word in keysAndValues)
		//                    if (word.Trim().StartsWith(toMatch))
		//                    {
		//                        found = true;
		//                        break;
		//                    }
		//            }
		//            else // must be in the middle then
		//            {
		//                string[] matchPair = filterString.Split('*');
		//                int end = matchPair.Length - 1;
		//                foreach (string word in keysAndValues)
		//                {
		//                    if (word.Trim().StartsWith(matchPair[0]) && word.Trim().EndsWith(matchPair[end]))
		//                    {
		//                        found = true;
		//                        break;
		//                    }
		//                }
		//            }
		//            if (!found)
		//                return false;
		//            #endregion
		//        }
		//        else if (!json.Contains(filterString))
		//            return false;
		//    }
		//    return true;
		//}
		///// <summary>
		///// list of strings, seperated by +, that show up at least once in the JSON representation of the setting (no wildcards)
		///// </summary>
		///// <param name="setting"></param>
		///// <param name="filter">i.e. (different results, + is the conjunction operator): "Gravity":"C" or Gravity+C </param>
		///// <returns>true if condition(s) match</returns>
		//protected virtual bool ContainsFilter(dynamic setting, string filter)
		//{
		//    if (string.IsNullOrWhiteSpace(filter))
		//        return true;
		//    string[] filterStrings = filter.Split(new char[] { '+', ' ' });
		//    string json = setting.ToString();
		//    foreach (string filterString in filterStrings)
		//        if (!json.Contains(filterString))
		//            return false;
		//    return true;
		//}
		/// <summary>
		/// compare method - overload this when more complex comparison is wanted
		/// </summary>
		/// <param name="x"></param>
		/// <returns>true, if it matches</returns>
		public virtual bool Matches(XmlSetting x)
		{
			return x.name == name;
		}
		public virtual bool Matches(SearchExpression se)
		{
			return se.IsMatch(this);
		}
		public virtual bool Matches(string filter, Compare comp = Compare.ByProperty)
		{
			if (comp == Compare.JSON)
			{
				if (string.IsNullOrWhiteSpace(filter))
					return true;
				string[] filterStrings = filter.Split(new char[] { '+', ' ' });
				string json = ToString();
				foreach (string filterString in filterStrings)
					if (!json.Contains(filterString))
						return false;
				return true;
			}
			var se = new SearchExpression(filter);
			return se.IsMatch(this);
		}
		public virtual object Clone()
		{	
			JObject o = JObject.FromObject(this);
			JObject oClone = (JObject)o.DeepClone();
			return oClone.ToObject<XmlSetting>();
		}
		/// <summary>
		/// merges a second setting into this setting; overload in derived classes; updates the dirty flag of this setting if second is younger (greater)
		/// </summary>
		/// <param name="second"></param>
		/// <remarks>this.dirty will be true after the call if second.dirty was true before the call AND second was modified more recently than this</remarks>
		public virtual void Merge(XmlSetting second)
		{
			// Name is identifying and must be the same
			if (Name != second.Name)
				throw new ArgumentException("Error: " + Name + ".Merge(" + second.Name + ") is an invalid call - Names must be equal.");
			if (Modified().UtcTicks == second.Modified().UtcTicks)	// was ==
			{
				dirty = false;	// identical means memory is up to date
				return;
			}
			if (Modified().UtcTicks <= second.Modified().UtcTicks)		// less means older => use all the property values from the second XmlSetting; RSB 20150603: <= enables repeated reading of change files
			{
				dirty = true; // FIX 20150602; RSB // !second.Valid();
				modified = second.modified;
				#region special treatment if second was just deleted
				if (second.deleted)	// special treatment
				{
					dirty = dirty || deleted != second.deleted;	// if memory setting became deleted, the flag has to be set to cause writing a change file on the calling level
					deleted = true;
				}
				else deleted = false;
				// examples: if deleted flags don't match the dirty flag has to be set; otherwise this.dirty becomes second.dirty
				// !second.deleted && !this.deleted
				// second.deleted && !this.deleted => dirty
				// second.deleted && this.deleted && !second.dirty => not dirty
				// second.deleted && this.deleted && second.dirty => dirty
				#endregion
				#region set all properties to values of the second object's properties
				foreach (var propertyInfo in this.GetType().GetProperties())
				{
					if (propertyInfo.CanWrite)
					{
						dynamic value;
						try
						{
							value = propertyInfo.GetValue(second, null);
							propertyInfo.SetValue(this, value, null);
						}
						catch (TargetParameterCountException)
						{
							// second does not have this property: use this
							try
							{
								value = propertyInfo.GetValue(this, null);
								propertyInfo.SetValue(this, value, null);
							}
							catch (TargetParameterCountException) 
							{ 
								//value = new Object();
								// init not possible
							}
						}
					}
				}
				#endregion
			}
			else dirty = true;	// means: this has the younger version, second is old => next XmlFile.Store() fixes this
			// the rest has to be done in the derived classes ... maybe not - TODO check if the reflection solution above works
			// currently the following behavior shows: Modified and second.Modified are never the same; therefore, each run creates a change file for every setting that is
			// supposedly up to date but has a different Modified anyway.
		}
		/// <summary>Constructor</summary>
		/// <param name="name"></param>
		/// <param name="comment"></param>
		/// <remarks>timestamp of this setting will be set to UtcNow after this</remarks>
		public XmlSetting(string name, string comment, bool invalidate = true)
		{
			this.name = name;
			Note = comment;
			if (invalidate)
				Invalidate();
		}
		/// <summary>copy-constructor</summary>
		/// <remarks>timestamp will be set to from's timestamp after this</remarks>
		public XmlSetting(XmlSetting from)
		{
			var clone = from.Clone(); 	// I might need a deep copy here (i.e. to have copies, not references of container contents)
			#region set all properties to values of the from object's properties
			foreach (var propertyInfo in this.GetType().GetProperties())
				if (propertyInfo.CanWrite)
					propertyInfo.SetValue(this, propertyInfo.GetValue(clone, null), null);
			#endregion
			modified = from.modified;	// setting the properties altered the timestamp; re-set it to from's 
		}
		/// <summary>Parameterless constructor</summary>
		/// <remarks>nothing will be set after this; leave everything (name, note and modified) for the serializer to set.
		/// This constructor is sort-of reserved for the use by the serializer; make sure to use any other constructor in your
		/// derived class, like :base(name, comment) to have the timestamp in modified initialized properly.
		/// If you want to use any other constructor as base class constructor for the parameterless constructor of your custom
		/// class XxxSetting, make sure to pass in invalidate: false. Otherwise merge would create erroneous results.
		/// see SubscriberSetting for an example
		/// </remarks>
		public XmlSetting()
		{
		}
	}

	/// <summary>This class refers to output format setting of images for the ImageServer Solution</summary>
	/// <remarks>mainly used by the ImageServer; storing, caching</remarks>
	public class XmlFile<T>
		where T : XmlSetting, new()
	{
		public static string Version {
			get
			{
				if (version == null)
				{
					System.Reflection.Assembly asm = System.Reflection.Assembly.GetExecutingAssembly();
					version = asm.GetName().Version.ToString(2);
				}
				return version;
			}
			set
			{
				version = value;
			}
		} private static string version = null;
		public static string ConfigDirDefault
		{
			get
			{
				if (configDirDefault == null)
					configDirDefault = Os.winInternal(Os.DropboxRoot + "Config" + Os.DIRSEPERATOR + Version + Os.DIRSEPERATOR/* + Environment.MachineName + Os.DIRSEPERATOR*/);
				return configDirDefault;
			}
			set
			{
				configDirDefault = value;
			}
		} private static string configDirDefault = null;
		public static string ChangeDirDefault
		{
			get
			{
				return ConfigDirDefault + typeof(T).Name.Replace("Setting", "s") + Os.DIRSEPERATOR;
			}
		}
		public static string ResourceDirDefault(string subscriber = null)
		{
			return ConfigDirDefault + 
				((subscriber == null) ? "" : (subscriber + Os.DIRSEPERATOR)) +
				typeof(T).Name.Replace("Setting", "Resources") + Os.DIRSEPERATOR;
		}
		private static int usingPersistence = 0;										// used by Interlocked
		static readonly object _locker = new object();							// used by Monitor ... the question is: why do I need both?
		#region Flag file
		/// <summary>
		/// Can be used to identify if the current server is master for this XmlFile
		/// </summary>
		/// <returns>true if current server has master rights to the file</returns>
		public bool RunningOnMaster()
		{
			return unflagged || (Master().Originator == Environment.MachineName);
		}
		private bool unflagged;
		public MasterFlagFile FileInfo() {
			if (fileInfo == null)
				fileInfo = new MasterFlagFile(ChangeDir, Environment.MachineName);
			if (fileInfo.Lines.Count == 0)	// means: we just created the file
				fileInfo.Update();
			return fileInfo;
		} private MasterFlagFile fileInfo = null;
		public MasterFlagFile Master()
		{
			master = new MasterFlagFile(ChangeDir, "Master"); // read it or create it
			if (string.IsNullOrEmpty(master.Originator))    // means: we just created the file master.info
				master.Update();    // takes ownership to this machine if no server is set yet
			return master;
		} private MasterFlagFile master = null;
		#endregion
		#region store and load options (to be set via constructor)
		public bool ReadOnly { get; set; }
		public bool Backup { get; set; }
		private Func<T, string> orderBy;
		private bool descending;
		#endregion
		/// <summary>
		/// The latest change in the memory (currently loaded or altered XmlSettings) - no file access
		/// </summary>
		/// <returns></returns>
		public DateTimeOffset GetMemChanged()
		{
			if (Infos == null)
				throw new FieldAccessException("Error in GetMemChanged(): Infos not initialized properly");
			return Infos.GetLastestSettingChanged();
		}
		/// <summary>
		/// Opens the file and retrieves the youngest XmlSetting in the file, identified by the XmlSetting's modified attribute
		/// </summary>
		/// <remarks>the FileInfo attributes (i.e. LastWriteTimeUtc) are totally irrelevant for this result 
		/// - this only cares about the modified attribute of the XmlSettings stored in the file</remarks>
		/// <returns>youngest XmlSetting's modified</returns>
		public DateTimeOffset GetFileChanged()
		{
			// TODO we might need a monitor or a semaphore here
			var diskSettings = new XmlFile<T>(Name, readOnly: true);
			return diskSettings.GetMemChanged();
		}
		/// <summary>
		/// Loads the file from disk to compare the XmlSetting.modified attribute of all XmlSettings stored in it
		/// </summary>
		/// <remarks>greater means younger</remarks>
		/// <returns>true, if the youngest setting on disk is younger than the youngest setting in memory</returns>
		public bool FileHasChangedOnDisk()
		{
			var info = new FileInfo(Name);
			if (!info.Exists)
				return false;
			return GetFileChanged() > GetMemChanged();
		}
		/// <summary>
		/// Changes from other servers are available when change files are there
		/// </summary>
		/// <returns>true if a reload seems necessary, false otherwise</returns>
		public bool ForeignChangesAvailable()
		{
			return (
				from _ in Directory.GetFiles(ChangeDir, "*.xml")
				where !(_).EndsWith("_" + Environment.MachineName + ".xml")
				select _
			).Count() > 0;
		}
		/// <summary>
		/// the XmlFile on disk is newer than the last file loaded into RAM
		/// </summary>
		/// <returns>true if a reload seems necessary, false otherwise</returns>
		public bool MasterUpdatesAvailable()
		{
			return Master().Time < FileInfo().Time;
		}
		/// <summary>
		/// Merges the XmlSettings on disk into the XmlSettings in memory
		/// - the dirty flags are set accordingly - can be checked using Invalid()
		/// - in WriteThrough mode the file on disk will be updated if necessary and all dirty flags will be cleared 
		/// </summary>
		/// <remarks>deprecated - use Reload</remarks>
		public void Merge(bool writeThrough = false)
		{
			var currentDiskFile = new XmlFile<T>(Name);
			foreach (var setting in currentDiskFile.Infos)
			{
				if (Infos.Contains(setting.Name, true))
					Infos[setting.Name].Merge(setting);
				else Infos[setting.Name] = setting;
			}
			if (writeThrough)	// checked inside Store: && Infos.Invalid()
				Store();
		}
		/// <summary>
		/// Directory for change files
		/// </summary>
		/// <remarks>new: change files are now on the top level, ie c:\Dropbox\3.5\Users\ or C:\Dropbox (HDitem)\3.6\demo\Overlays\</remarks>
		public string ChangeDir
		{
			get
			{
				var file = new RaiFile(Name);
				file.mkdir();
				file.Path = file.Path + file.Name;
				return file.Path;
			}
		}
		/// <summary>
		/// Directory that contains referenced files, i.e. Bargain.png for XmlFile&lt;OverlaySetting>
		/// </summary>
		public string ResourceDir
		{
			get
			{
				var file = new RaiFile(Name);
				return Os.NormSeperator(file.Path.Replace(Os.DIRSEPERATOR + Environment.MachineName, "") + typeof(T).Name.Replace("Setting", "Resources") + Os.DIRSEPERATOR);
			}
		}
		/// <summary>
		/// A change file is a file that contains just one setting that can me merged into a file with many settings
		/// </summary>
		/// <param name="Setting"></param>
		/// <param name="Machine">any machine name different from Environment.MachineName</param>
		/// <remarks>
		/// new: 2014-11-24 one change file must be sufficient for all servers, Dropbox distributes it;
		/// make sure change file name contains the server who originated the change
		/// for this to make sense, the XmlFile (with all settings) has to be located inside a dropbox and it's path has to contain the Environment.MachineName
		/// old: D:\Dropbox\Config\3.3.3\U17138031\Servers.xml with change file D:\Dropbox\Config\3.3.3\Titan562\Servers\U17138031_635284457032693173.xml
		/// new: D:\Dropbox\Config\3.6\Users.xml with change file D:\Dropbox\Config\Users\635284457032693173_U17138031.xml
		/// </remarks>
		public void CreateChangeFile(T Setting, string MachineName = null)
		{
			if (MachineName == null)
				MachineName = Environment.MachineName;
			var settings = new List<T>();
			settings.Add(Setting);
			var changeFile = new RaiFile(ChangeDir + Setting.Modified().UtcTicks.ToString() + "_" + MachineName + ".xml");
			if (!File.Exists(changeFile.FullName))	// if the same file already exists it must contain the same change => no need to duplicate it
				new XmlFile<T>(changeFile.FullName, new XmlSettings<T>(settings), unflagged: true).Save();
		}
		private string xmlFileName;
		public string Name
		{
			get { return xmlFileName; }
			set { xmlFileName = value; }
		}
		private XmlSettings<T> infos;
		public XmlSettings<T> Infos
		{
			get { return infos; }
			set { infos = value; }
		}
		/// <summary>
		/// Set modified if null; also executes Validate() for each setting
		/// </summary>
		private void MakeSureEachSettingHasValidTimestamp()
		{
			foreach (var name in Infos.SettingNames)
			{
				if (Infos[name].modified == null)
					Infos[name].modified = DateTime.MinValue;	// treat setting without modified as set at the beginning of time
			}
			// this only runs directly after Load() => every setting is suposed to be !dirty
			foreach (var setting in Infos)
				setting.Validate();
		}
		/// <summary>
		/// Load Xml file
		/// </summary>
		public void Load(bool undercover = false)
		{
			// HACK: System.Xml.Serialization.XmlSerializer still can't serialize Dictionary<TKey, TData> - use other serializers here or 
			// make sure that the classes you want to serialize do contain something else and construct a Dictionary from this sth else
			var xmlSerializer = new System.Xml.Serialization.XmlSerializer(typeof(T[]));	// TODO use ServiceStack.Text.XmlSerializer instead - should be faster
			if (0 == Interlocked.Exchange(ref usingPersistence, 1))	// that might be too much (but it certainly is thread-safe)
			{
				try
				{
					using (TextReader reader = new StreamReader(xmlFileName))
					{
						infos.Settings = (T[])xmlSerializer.Deserialize(reader);
						MakeSureEachSettingHasValidTimestamp();
					}
				}
				catch (InvalidOperationException)
				{
					throw;
				}
				finally
				{
					if (!(undercover || unflagged))
						FileInfo().Update(Infos.GetLastestSettingChanged());
					Interlocked.Exchange(ref usingPersistence, 0);
				}
			}
		}
		public List<T> LoadJson()
		{
			var xmlFile = new RaiFile(xmlFileName);
			xmlFile.Ext = "json";
			var tf = new TextFile(xmlFile.FullName);
			string s = string.Join("", tf.Lines);
			fileInfo.Time = DateTimeOffset.UtcNow;
			return s.FromJSON<T[]>().ToList();
		}
		/// <summary>
		/// Loads all changes and updates the file if necessary; run after Load
		/// </summary>
		/// <remarks>only the master is supposed to alter the main file or to delete change files; 
		/// new: change files now also have to be at least 10 min old to give all Dropbox instances a chance to do their sync
		/// and: only the master is allowed to perform deletion of ChangeFiles or updating of the main file on disk</remarks>
		public void MergeChanges()
		{
			var changes = new XmlSettings<T>();
			#region collect changes
			XmlFile<T> xmlFile;
			if (Directory.Exists(ChangeDir))
			{
				foreach (var file in Directory.GetFiles(ChangeDir, "*.xml").OrderByDescending(x => x))	// gets the latest first => less work if several changes exist
				{
					try
					{
						xmlFile = new XmlFile<T>(file, undercover: true, readOnly: true);	// change files are not supposed to be edited (write-once, no-update); RSB 2015-10-06
						var cSet = xmlFile.Infos.First();
						if (changes.Contains(cSet.Name))
						{
							if (cSet.Modified() > changes[cSet.Name].Modified())
							{	// ok this one is newer, replace the one from before
								changes.Delete(cSet.Name);
								changes.Add(cSet, true);
							}
							// otherwise: just ignore this setting - there is a newer one already
						}
						else changes.Add(xmlFile.Infos.First(), true);	// as designed, change files only contain one setting - TODO how to detect the same change in two different change files?
						if (RunningOnMaster() && (DateTimeOffset.UtcNow - new FileInfo(file).CreationTime).TotalSeconds > 600)
							if (!ReadOnly)
								new RaiFile(file).rm();	// savely remove the file from the dropbox unless in ReadOnly mode
					}
					catch (System.InvalidOperationException)
					{
					}
				}
			}
			#endregion
			#region apply changes to 
			foreach (var changedSetting in changes)
			{
				if (Infos.Contains(changedSetting.Name, true))
				{	// Merge handles the dirty flag
					Infos[changedSetting.Name].Merge(changedSetting);	// TODO double check modified and dirty flag
					//Infos[changedSetting.Name] = changedSetting;
				}
				else
				{	// new setting from disk means dirty flag has to be set, timestamp has to be preserved
					changedSetting.Invalidate(preserveTimestamp: true);
					Infos[changedSetting.Name] = changedSetting;
				}
			}
			if (!ReadOnly)
				Store();	// changes the disk file if a change file existed with a newer version of a setting or if i.e. a ram setting was changed/deleted
			#endregion
		}
		/// <summary>
		/// Reloads an XmlFile and all it's changes from disk if necessary (changes this.Infos)
		/// </summary>
		/// <returns>true if a reload was performed (and Infos was changed), false otherwise (Infos unchanged)</returns>
		public bool Reload()
		{
			bool masterUpdates = MasterUpdatesAvailable();
			bool foreignChanges = ForeignChangesAvailable();
			//bool ownChanges = this.Infos.Invalid();
			if (masterUpdates && RunningOnMaster())
				throw new Exception("Some process changed the main file without permission => inconsistent data in " + this.Name + ", file " + this.xmlFileName);
			if (masterUpdates)
			{	// we need the new master; let's save our changes first
				Save();	// creates change files with changes that might loose if the master also has the same setting changed
				Load();	// now we are guaranteed to have the latest stuff including all foreign changes
				return true;
			}
			if (!masterUpdates && foreignChanges)
			{ // just read the change files, not the main file; the settings from the main file we already have in memory
				MergeChanges();	// whatever comes from disk is valid thereafter; a newer change from disk removes a older change in memory
				//Save();	// no, doppelgemoppelt; MergeChanges performs a Save at the end if Invalid()
				return true;
			}
			return false;
		}
		/// <summary>makes File persistent - thread safe</summary>
		/// <param name="backup">backs up current xml file if true, or not if false, or uses the setting passed in to the constructor if null</param>
		/// <param name="force">todo: describe force parameter on Save</param>
		/// <remarks>function varys between master and others: master can store to the actual file - others can only create change files
		/// </remarks>
		public void Save(bool? backup = null, bool force = false)
		{
			if (backup != null)
				Backup = (bool)backup;
			if (ReadOnly)
				throw new IOException("XmlFile " + Name + " was set to readonly mode but an attempt was made to execute XmlFile.Save");
			Monitor.Enter(_locker);
			try
			{
				if (RunningOnMaster())
					Store(force);
				else CreateChangeFiles();
			}
			finally
			{
				Monitor.Exit(_locker);
			}
		}
		/// <summary>
		/// SaveJson - export a Settings file as Items (always writes)
		/// </summary>
		/// <param name="backup"></param>
		public void SaveJson(bool backup = false)
		{
			if (Infos == null)
				return;
			if (ReadOnly)
				throw new IOException("XmlFile " + Name + " was set to readonly mode but an attempt was made to execute XmlFile.StoreJson");
			var diskFile = new RaiFile(Name);
			diskFile.Ext = "json";
			if (backup)
				diskFile.backup();
			else diskFile.rm();
			var tf = new TextFile(diskFile.FullName);
			tf.Append(Infos.ToJSON());
			tf.Save();
		}
		/// <summary>
		/// Reads a valid xml file (*.xml) and writes the corresponding *.json file
		/// </summary>
		/// <returns>false if target document alread existed</returns>
		public bool Convert2JsonFile()
		{
			var jsonFile = new RaiFile(Name);
			jsonFile.Ext = "json";
			var xmlFile = new RaiFile(Name);
			xmlFile.Ext = "xml";
			if (xmlFile.Exists())
				return false;  // do not override an existing file
			var xf = new TextFile(xmlFile.FullName);
			var xml = string.Join("", xf.Read());
			var doc = new XmlDocument();
			doc.LoadXml(xml);
			var json = JsonConvert.SerializeXmlNode(doc);
			var jf = new TextFile(xmlFile.FullName);
			jf.Append(json);
			jf.Save();
			return true;
		}
		/// <summary>
		/// Save all changes as a single change file per setting
		/// </summary>
		/// <remarks>To determine what has changed, reloads the disk file's settings into a second variable and compares one by one with the ones in this.Infos.
		/// Writes a ChangeFile for a setting only if it is newer than the setting from disk. The time stamp of the change file has to be the modified time stamp in the setting.
		/// Does not care about settings that are newer on disk.</remarks>
		/// <seealso cref="Load"/>
		/// <seealso cref="MergeChanges"/>
		private void CreateChangeFiles()
		{
			var compareFile = new XmlFile<T>(Name, undercover: true);	// if undercover, Load does not update the time stamp in the flag file for the local server, ie RAPTOR133.info
			foreach (var memSetting in Infos)
			{
				if (!compareFile.Infos.Contains(memSetting.Name, withDeleted: true)					// memSetting does not exist on disk
					|| (memSetting.Modified() > compareFile.Infos[memSetting.Name].Modified()))	// memSetting is newer than diskSetting
					CreateChangeFile(memSetting);
			}
		}
		/// <summary>makes XmlFile persistent; performs merge on setting level</summary>
		/// <param name="force">todo: describe force parameter on Store</param>
		protected void Store(bool force = false)
		{
			if (Infos == null)
				return;
			var diskFile = new RaiFile(Name);
			#region not doing implicit merge anymore inside Store() - call Merge() instead
			//if (FileHasChangedOnDisk())
			//{
			//	var diskSettings = new XmlFile<T>(Name).Infos;		// get the content first, then backup
			//	#region synchronize file with memory; Invalidate memory if necessary
			//	foreach (var diskSetting in diskSettings)
			//		if (Infos.Contains(diskSetting.Name, withDeleted: true))
			//			Infos[diskSetting.Name].Merge(diskSetting);
			//		else Infos.Add(diskSetting, preserve: true);
			//	//Infos.FileTimeOnLoad = diskSettings.FileTimeOnLoad;	// makes sure that subsequent calls to FileHasChangedOnDisk() return false
			//	#endregion
			//}
			// TODO Merging a changed file into the memory appearently clears the dirty flag ... but is the content stored to file
			// wrong thinking: it clears the dirty flag for younger Settings loaded from disk; if a setting is younger in memory the dirty flag stays set
			#endregion
			#region only save if necessary
			if (force || Infos.Invalid())
			{
				if (ReadOnly)
					throw new IOException("XmlFile " + Name + " was set to readonly mode but an attempt was made to execute XmlFile.Store");
				Exception inner = null;
				System.Xml.Serialization.XmlSerializer xs = null;	// TODO check if ServiceStack.Text.XmlSerializer is better here; check license fee https://servicestack.net/download#free-quotas
				try
				{
					xs = new System.Xml.Serialization.XmlSerializer(typeof(T[]));	// Q: List<T> oder T[]
				}
				catch (Exception SerializerException)
				{
					inner = SerializerException.InnerException;
					// TODO that's not good; needs to write a error entry into log at least
					return;
				}
				var settings = Infos.Settings;
				if (settings.Count() > 0)	// passing in empty settings is not a valid way to delete the content of a settings file; silently refuse storing a new version
				{
					if (Backup)
						diskFile.backup();
					else diskFile.rm();
					new RaiFile(Name).mkdir();	// does nothing if dir was there; otherwise creates the dir and awaits materialization
					if (orderBy != null)
						settings = descending ? settings.OrderByDescending(orderBy).ToArray() : settings.OrderBy(orderBy).ToArray();
					using (StreamWriter sw = new StreamWriter(Name))
					{
						xs.Serialize(sw, settings);
						// TODO the memory representation is dated Infos.FileTimeOnLoad - what is the diskSettings.FileTimeOnLoad timestamp?
						// after a store this timestamp must be the same, otherwise a subsequent FileHasChangedOnDisk() call would return the wrong result
						// FIX make sure it is the same after the creation of a settings file (also after backing up the old one)
						// FIX make sure it is Dropbox safe
					}
					var changeTime = Infos.GetLastestSettingChanged();
					File.SetLastWriteTimeUtc(Name, changeTime.DateTime);
					if (!unflagged)
					{
						Master().Time = changeTime;	// set the new XmlFile timestamp to the flag file master.info
						FileInfo().Time = changeTime;	// make sure the server's flag file also has the date current
					}
					#region Validate each setting
					// 	means: this setting has been stored to disk; however, in a concurrent environment it does not mean, 
					// that the setting on disk still has the same value as the one in memory since it could have been changed 
					// elsewhere and synchronized back to this machine
					foreach (var setting in Infos.Settings)
						setting.Validate();
					#endregion
				}
				else
				{
					throw new IOException("attempt to write a settings file with no settings into "
					+ diskFile.FullName + "; the current memory representation should reflect the old file's settings which it does not!", inner);
				}
			}
			#endregion
			//Infos.Validate();
		}
		/// <summary>Access a setting of the Infos via the setting's name</summary>
		/// <param name="settingName">indexer; value.name will be settingName after this</param>
		/// <remarks>acts as a business object; writing to external storage is initiated automatically if set to a new value
		///	you can write "xf["New Setting"] = oldSetting;" to create a copy of oldSetting with the new Name "New Setting"
		/// </remarks>
		/// <returns>object or default(T); compare to Infos[settingName]</returns>
		/// <exception cref="FieldAccessException"></exception>
		/// <value>the value can be null ... in fact, default(T) will be null for all Nullables; therefore, you cannot write File["123"].SomeProperty 
		/// since null.SomeProperty would throw an exception</value>
		public T this[string settingName]
		{
			get
			{
				if (!Infos.Contains(settingName, withDeleted: false))
					return default(T);
				return Infos[settingName];
			}
			set
			{
				if (string.IsNullOrEmpty(settingName))		// no-nonsense plausi check: make sure the used key is equal to Name
					throw new FieldAccessException("Constraint violation: indexer cannot be an empty string");
				Infos[settingName] = value;						// sets modified there
				Infos[settingName].name = settingName;	// nonsense correction constraint
			}
		}
		/// <summary>
		/// return a copy
		/// </summary>
		/// <param name="settingName"></param>
		/// <returns>a copy of the setting stored in the container</returns>
		public T GetSetting(string settingName)
		{
			if (!Infos.Contains(settingName))
				return default(T);
			return Infos.Get(settingName);
		}
		/// <summary>
		/// Factory method to eventually create an XmlFile of type T if changes are on disk
		/// </summary>
		/// <param name="fileName"></param>
		/// <param name="key"></param>
		/// <param name="subscriber"></param>
		/// <param name="orderBy"></param>
		/// <param name="descending"></param>
		/// <returns>null or an XmlFile of type T</returns>
		public static XmlFile<T> RefreshedFile(string fileName = null, string key = null, string subscriber = null, Func<T, string> orderBy = null, bool descending = false)
		{
			var vsf = new XmlFile<T>(fileName: fileName, key: key, subscriber: subscriber, orderBy: orderBy, descending: descending, autoload: false);	// make sure the files is not loaded unneccesarily
			if (vsf.RunningOnMaster())
			{
				if (vsf.ForeignChangesAvailable())
				{
					vsf = new XmlFile<T>(fileName: fileName, key: key, subscriber: subscriber, orderBy: orderBy, descending: descending, autoload: true);
					return vsf;
				}
			}
			else
			{
				if (vsf.MasterUpdatesAvailable())
				{
					vsf = new XmlFile<T>(fileName: fileName, key: key, subscriber: subscriber, orderBy: orderBy, descending: descending, autoload: true);
					return vsf;
				}
			}
			return null;
		}
		/// <summary>
		/// Factory method to check for newer infos on disk
		/// </summary>
		/// <param name="values">sth that can be casted to (XmlSettings<T>)</param>
		/// <param name="fileName"></param>
		/// <param name="key"></param>
		/// <param name="subscriber"></param>
		/// <param name="orderBy"></param>
		/// <param name="descending"></param>
		/// <returns>the passed in values if nothing newer is available on disk; the latest setting from the disk otherwise</returns>
		public static XmlSettings<T> LatestSettings(object values, string fileName = null, string key = null, string subscriber = null, Func<T, string> orderBy = null, bool descending = false)
		{
			var refreshed = XmlFile<T>.RefreshedFile(fileName, key, subscriber, orderBy, descending);
			if (refreshed != null)
				return refreshed.Infos;
			return (XmlSettings<T>)values;
		}
		/// <summary>constructor</summary>
		/// <param name="fileName">save/load location, full name with path; standard file name and path if omitted; standard file name will be appended if just path</param>
		/// <param name="values">values that already exist in memory; will be merged with existing settings</param>
		/// <param name="key"></param>
		/// <param name="orderBy">if skipped, x => x.Name</param>
		/// <param name="descending"></param>
		/// <param name="undercover"> if undercover, Load does not update the time stamp in the flag file for the local server, ie RAPTOR133.info; also: does not pick up the change files</param>
		/// <param name="unflagged">do not write flag file ie master.info, RAPTOR133.info, ...</param>
		/// <param name="readOnly">changed to default value true; please make this parameter explixit if possible</param>
		public XmlFile(string fileName = null, XmlSettings<T> values = null, string key = null, string subscriber = null, Func<T, string> orderBy = null, bool descending = false, 
			bool readOnly = true, bool backup = false, bool undercover = false, bool unflagged = false, bool autoload = true)
		{
			this.unflagged = unflagged;
			Backup = backup;
			ReadOnly = readOnly;
			this.orderBy = orderBy ?? new Func<T, string>(x => x.Name);
			this.descending = descending;
			Name = Os.NormSeperator(fileName ?? defaultFileName(subscriber));
			if (Name.EndsWith(Os.DIRSEPERATOR))
			{
				var f = new RaiFile(defaultFileName(subscriber));
				f.Path = Name;
				Name = f.FullName;
			}
			infos = new XmlSettings<T>((subscriber != null ? subscriber + "." : "") + (key ?? new RaiFile(Name).Name));
			#region values treatment
			// values were passed-in as a reference
			// therefore, Load changes values which means that it waives all changes 
			// => create a copy (maybe only with the ones with the dirty flag set)
			List<T> memoryChanges = null;
			if (values != null)
				memoryChanges = (
					from memSet in values
					where !memSet.Valid()
					select memSet
				).ToList();	// ToList should be creating a copy and not just keep a reference => check it out in the debugger!!!
			#endregion
			if (autoload)	// new option autoload: false reduces file io if Reload is not necessary
			{
				if (File.Exists(Name))
					Load(undercover);	// does not pick up the change files
				MergeChanges();	// this one does, calls Store()
				#region replaces Merge()
				if (memoryChanges != null)
				{
					foreach (var setting in memoryChanges)
					{
						if (Infos.Contains(setting.Name, true))
							Infos[setting.Name].Merge(setting);	// Merge() handles the dirty flag
						else
						{
							Infos[setting.Name] = setting;
							Infos[setting.Name].Invalidate(preserveTimestamp: true);		// new in mem => dirty
						}
					}
					if (Infos.Invalid())	// can happen if the loop above found newer settings in values
						Save();	// writes main file or changeFiles
				}
				#endregion
			}
		}
		/// <summary>
		/// defaultFileName with or without subscriber
		/// </summary>
		/// <param name="subscriber"></param>
		/// <returns></returns>
		public static string defaultFileName(string subscriber)
		{
			var file = new RaiFile(typeof(T).Name.Replace("Setting", "s") + ".xml");
			file.Path = XmlFile<T>.ConfigDirDefault;
			if (!string.IsNullOrWhiteSpace(subscriber))
				file.Path = file.Path + subscriber;
			return file.FullName;
		}
	}
	public class XmlSettingsBase
	{
		/// <summary>Identifying name, i.e. xmlFileName from enclosing XmlFile</summary>
		[XmlAttribute("key")]
		public string key;
		public XmlSettingsBase(string key = null)
		{
			this.key = key;
		}
	}
	/// <summary>Structure that keeps all template data</summary>
	[XmlRoot("XmlSettings")]
	public class XmlSettings<T> : XmlSettingsBase, IEnumerable<T>
		where T : XmlSetting, new()
	{
		/// <summary>
		/// Checks settings in the container
		/// </summary>
		/// <returns>true if any is not Valid, false if none</returns>
		public bool Invalid()
		{
			//foreach (var setting in Settings)
			//	if (!setting.Valid())
			//		return true;
			//return false;
			// the following implementation is as fast as the one before if no setting is invalid and slower if more than none are
			var query = from setting in Settings where !setting.Valid() select setting.Name;
			return query.Count() > 0;
		}
		/// <summary>
		/// latest change - does not consider the surrounding container, just the settings
		/// </summary>
		/// <returns>timestamp, DateTimeOffset.MinValue of none</returns>
		public DateTimeOffset GetLastestSettingChanged()
		{
			var list = (from _ in Settings select _.Modified());
			list = list.OrderByDescending(x => x);
			return list.Count() > 0 ? list.First() : DateTimeOffset.MinValue;
		}
		/// <summary>
		/// find a template setting in tList
		/// </summary>
		/// <param name="settingName">name of template</param>
		/// <param name="defaultTemplate">tries to find this one if template could not be found; 
		///		if null: create one using the standard constructor and also set its modified to DateTimeOffset.MinValue</param>
		/// <returns>the templateSetting with the given name if found, or the default setting or an empty setting</returns>
		/// <remarks>always returns a setting; does not throw an exception</remarks>
		public T findSetting(string settingName, string defaultTemplate = null)
		{
			T defTmp = null, t = null;
			foreach (KeyValuePair<string, T> setting in settings)
			{
				if (setting.Key.Equals(settingName))
				{
					t = setting.Value; // new SettingClass(setting);
					break;
				}
				if (!string.IsNullOrEmpty(defaultTemplate) && setting.Key.Equals(defaultTemplate))
					defTmp = setting.Value; // new SettingClass(setting);
			}
			if (t != null)
				return t;
			if (defTmp != null)
				return defTmp;
			var newT = new T();
			newT.modified = DateTime.MinValue;
			return newT;
		}
		internal ConcurrentDictionary<string, T> settings = new ConcurrentDictionary<string, T>();
		/// <summary>includes deleted settings; Settings.Length ignored deleted settings</summary>
		public int Count
		{
			get { return settings.Count; }
		}
		/// <summary>add one setting at the end</summary>
		/// <param name="item"></param>
		/// <param name="preserve">set true, if you want to add a new or recovered setting and preserve the item's deleted and modified</param>
		public void Add(T item, bool preserve = false)
		{
			if (!preserve)
			{
				item.deleted = false;
				item.modified = DateTimeOffset.UtcNow.UtcDateTime;
			}
			settings[item.name] = item;
			//Invalidate();
		}
		/// <summary>
		/// create a copy of the setting
		/// </summary>
		/// <param name="name"></param>
		/// <param name="mode"></param>
		/// <returns>name of the duplicate</returns>
		public string Duplicate(string name)
		{
			#region find a name that does not exist yet
			int copyNumber = 2;
			while ((from _ in this where _.Name == (name + copyNumber.ToString()) select _).Count() > 0)
				copyNumber++;
			#endregion
			T copy = (T)this[name].Clone();
			copy.name = name + copyNumber.ToString();
			copy.Invalidate();
			Add(copy);
			return copy.name;
		}
		/// <summary>delete one setting</summary>
		/// <param name="item"></param>
		public bool Remove(T item)
		{
			T value;
			var result = settings.TryRemove(item.name, out value);
			//if (result)
			//	Invalidate();
			return result;
		}
		/// <summary>logical delete; sets deleted flag</summary>
		/// <param name="itemName"></param>
		/// <param name="by">who deleted this setting; used to write by into the setting's Note</param>
		/// <returns>true: did not exists or is marked as deleted now</returns>
		public bool Delete(string itemName, string by = null, bool backDate = true)
		{
			if (string.IsNullOrEmpty(itemName))
				return true;
			try
			{
				settings[itemName].Delete(by, backDate);	// will not delete a deleted setting again (conserves timestamp and note)
			}
			catch (KeyNotFoundException) { }
			catch (Exception)
			{
				return false;
			}
			return true;
		}
		/// <summary>
		/// Update means the setting has changed or is treated as if it has
		/// </summary>
		/// <param name="oldName"></param>
		/// <param name="newSetting"></param>
		/// <param name="by"></param>
		/// <returns></returns>
		public bool Update(string oldName, T newSetting, string by = null)
		{
			newSetting.Invalidate();
			if (oldName == newSetting.Name)
			{
				newSetting.Invalidate();
				this[oldName] = newSetting;
				return true;
			}
			if (this.Contains(newSetting.Name))
				return false;	// cannot change the name to an already existing name (name is key and must be unique)
			#region key has changed - delete oldSetting first
			this.Delete(oldName, by, backDate: true);
			newSetting.Invalidate();
			Add(newSetting);
			#endregion
			return true;
		}
		/// <summary>Get or set whole List; list contains all elements including deleted ones</summary>
		/// <remarks>use SettingNames or Select to get a list without deleted entries</remarks>
		[XmlElement("XmlSettings")]
		public T[] Settings
		{
			get { return (from _ in settings /*where _.Value.deleted != true*/ select _.Value).ToArray(); }
			set
			{
				settings = new ConcurrentDictionary<string, T>();
				if (value != null)
				{
					#region remove duplicates in value manually - the last one wins
					foreach (var kvp in value)
						if (settings.ContainsKey(kvp.name))
						{
							var x = settings[kvp.name];
							if (x.modified > kvp.modified)
							{
								x.Note += "\n{ duplicate entry with modification time " + kvp.modified.ToString("o") + " removed }";
								settings[kvp.name] = x;
							}
							else
							{
								kvp.Note += "\n{ duplicate entry with modification time " + x.modified.ToString("o") + " removed }";
								settings[kvp.name] = kvp;
							}
						}
						else settings[kvp.name] = kvp;
					#endregion
				}
			}
		}
		/// <summary>Access a setting via the setting's name</summary>
		/// <param name="settingName">name</param>
		/// <remarks>acts as a business object; writing to external storage is initiated automatically if set to a new value</remarks>
		/// <returns>object or throws KeyNotFoundException</returns>
		public T this[string settingName]
		{
			get
			{
				return settings[settingName]; 
			}
			set
			{
				if (string.IsNullOrWhiteSpace(value.name))
					value.name = settingName;
				settings[settingName] = value;
				//Invalidate();	// changes the timestamp in the container - does not affect the setting
				if (value.Modified() != settings[settingName].Modified())
					throw new InvalidDataException("Error: inserting/updating a setting into a container is not supposed to change the setting's timestamp");
			}
		}
		/// <summary>
		/// Access a setting
		/// </summary>
		/// <param name="settingName">index</param>
		/// <returns>a cloned copy of the setting stored in the collection</returns>
		public T Get(string settingName)
		{
			return (T)settings[settingName].Clone();
		}
		public bool Contains(string settingName, bool withDeleted = false)
		{
			var isThere = settings.ContainsKey(settingName);
			if (withDeleted)
				return isThere;
			return isThere && !settings[settingName].deleted;
		}
		/// <summary>
		/// lists the names of all settings that are not deleted
		/// </summary>
		public string[] SettingNames
		{
			get { return (from _ in settings where _.Value.deleted != true select _.Value.name).ToArray(); }
		}
		/// <summary>select settings indicated by passed-in names</summary>
		/// <param name="settingNames">identifying names</param>
		/// <remarks>throws an exception if one of the requested settings does not exist</remarks>
		/// <returns>array</returns>
		public T[] Select(string[] settingNames)
		{
			List<T> list = new List<T>();
			T elem;
			foreach (string oName in settingNames)
			{
				elem = this[oName];
				if (!elem.deleted)
					list.Add(this[oName]);
			}
			return list.ToArray();
		}
		/// <summary>Selects all items that are not deleted and contain the filter</summary>
		/// <param name="filter">string that can contain wildcard and field identifiers</param>
		/// <returns>array</returns>
		public T[] Select(string filter, Compare comp = Compare.ByProperty)
		{
			return (from setting in settings
					where !setting.Value.deleted && setting.Value.Matches(filter, comp)
					select setting.Value).ToArray();
		}
		public T[] Select(string filter, int pageIndex, int pageSize, out int totalRecords)
		{
			// TODO OrderBy(x => x.name) or better 
			var all = (from setting in settings
					   where !setting.Value.deleted && setting.Value.Matches(filter)
					   select setting.Value);
			totalRecords = all.Count();
			return all.ToArray();
			// TODO Skip(pageIndex * pageSize).Take(pageSize).
			// TODO check if pageIndex runs from 0 or from 1
		}
		#region IEnumerable implementation
		private List<T> _collection = new List<T>();
		/// <summary>creates a List of the Value entries in the Dictionary</summary>
		/// <remarks>be aware that this method creates a new list everytime it is called; 
		/// only use it when you need the values in a List - otherwise access settings;</remarks>
		/// <example>use this.Count instead of this.Count() since it consumes less ressources</example>
		/// <returns></returns>
		public IEnumerator<T> GetEnumerator()
		{
			_collection = new List<T>(from _ in settings select _.Value);
			return _collection.GetEnumerator();
		}
		IEnumerator IEnumerable.GetEnumerator()
		{
			return GetEnumerator();
		}
		#endregion
		/// <summary>Copy constructor</summary>
		public XmlSettings(XmlSettings<T> from)
			: this(from.key)
		{
			//dirty = false;
			Settings = from.Settings;	// deep copy since from.Settings creates an Array
		}
		public XmlSettings(IEnumerable<T> settings, string key = null)
			: this(key)
		{
			Settings = null;
			foreach (var x in settings)
				this.settings[x.name] = x;
		}
		/// <summary>Constructor</summary>
		public XmlSettings(string key)
			: base(key)
		{
			Settings = null;
		}
		public XmlSettings()
		{
		}
	}
}

