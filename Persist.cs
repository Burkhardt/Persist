using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
//using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;
//using System.Web.Script.Serialization;
//using HDitem.Utilities;
using RaiUtilsCore;
using OperatingSystemCore;
using JsonPitCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// NEW ATTEMPT to fix some new issues, in particular too many files/copies of the main file
// Only the master server writes the main file (ie Users.aspx) - a flag file master.info can exist; if it doesn't any server can write to the JsonFile. However, if it does exist, 
// only the server mentioned there is allowed to write to the JsonFile. The flag file also contains a timestamp which contains the last time the JsonFile was saved.
// A second flag [MachineName].info file exists as soon as a server saves any changes. The changes will not be written back to the JsonFile but a ChangeFile will be created instead.
// The second flag file contains the name of the server (that it is named after) and a timestamp that contains the time that this server last loaded the JsonFile into it's memory.
// Many other servers can read the file from the very same location in the Dropbox (ie ~Dropbox/Config/3.6/Users.json); every time they do the second flag file will be updated (or created if it hasn't existed yet).
// The master maintains a file that contains the name of the current master and a timestamp for the last save/merge (ie ~Dropbox/Config/3.6/Users/Master.json)
//    => changing the master in the file by hand changes the master
// Every server creates and maintains a file named after the server and with the server's name and a timestamp in it when the file was last loaded (ie ~Dropbox/Config/3.6/Users/Pro1997.json)
// The local server can read the Master.json to see if the file on disk is newer than the last file loaded which is the file in memory
// When the local server changes a setting, a change file will be written to the ChangeDir (ie ~Dropbox/Config/3.6/Users/20141124xxxxx_Pro1997.json)
// After the master has merged the changes into the main file, all change files older than 15 min will be deleted.
// When any other server opens the main file it also has to merge all existing change files but can never delete any. If a master is not working or the Synchronization fails the amount of 
//    change files will stack up and an operator has to fix the synchronization, make sure the Master ist working or switch to a different master (see Master.json). Ideally, the amount of 
//    change files is always small. If the number of changes per 15 min tends to become high => reconsider
// Result: Remote changes come in eventually, local changes are always current. If a conflict between a remote change and a local change comes up, the conflict will be resolved 
//    eventually and the change with the newest timestamp will prevail. All Timestamps are taken in universal time and all servers are expected to have a time synchronizing service running
//    so that no server get an unfair advantage.

// LATEST ATTEMPT to get this solved
// see detailled description in HDitem\ImageServer\doc\SynchronizedSettings\index.html
// every machine has it's own copy in a seperate dropbox dir named after the machine, delta files will be written to all server subdirectories on create/update/delete
// delta files will be inserted into the settings file the next time a Mem<T>-constructor will be called on this machine

// NEW APPROACH - busted (RSB, 2014-02-20)
// the LastWriteTimeUtc (of the JsonFile) will be set to the modified date of the youngest XmlSetting
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
 * JsonFile<T> supports synchronization via CloudDrive i.e. Dropbox.
 * 
 * see SyncFile.cs, idea 3 for a high traffic self-synchronization version of JsonFile.cs
 * 
 */

namespace Persist
{
	public static class DynamicHelper
	{
		/// <summary>
		/// returns the latest date/time of the two, based on universal time
		/// </summary>
		/// <param name="a">this timestamp</param>
		/// <param name="b">the parameter timestamp to compare with</param>
		/// <example>var latest = yesterday.OrIfLater(today);</example>
		/// <returns>the most recent of the two timestamps</returns>
		/// <remarks>see http://stackoverflow.com/questions/36464693/how-to-compare-timestamps-between-different-time-zones </remarks>
		public static DateTimeOffset OrIfLater(this DateTimeOffset a, DateTimeOffset b)
		{
			return a > b ? a : b;
		}
		public static string ToJSON(this object obj)
		{
			//return ServiceStack.Text.JsonSerializer.SerializeToString(obj);
			return JsonConvert.SerializeObject(obj);
		}
		public static T FromJSON<T>(this string obj)
		{
			//return ServiceStack.Text.JsonSerializer.DeserializeFromString<T>(obj.ToString());
			return JsonConvert.DeserializeObject<T>(obj);
		}
		//public static string ToXML(this object obj)
		//{
		//	return ServiceStack.Text.XmlSerializer.SerializeToString(obj);  // TODO use Json.NET
		//}
	}
	public enum Sync { Auto, UpdateFile, UpdateMem };
	/// <summary>This class refers to output format item of images for the ImageServer Solution</summary>
	/// <remarks>mainly used by the ImageServer; storing, caching</remarks>
	public class JsonFile<T> : JsonPitBase
		where T : Item, new()
	{
		#region T dependent defaults
		public static string ChangeDirDefault
		{
			get
			{
				return ConfigDirDefault + typeof(T).Name.Replace("Item", "s") + Os.DIRSEPERATOR;
			}
		}
		public static string ResourceDirDefault(string subscriber = null)
		{
			return ConfigDirDefault +
				((subscriber == null) ? "" : (subscriber + Os.DIRSEPERATOR)) +
				typeof(T).Name.Replace("Item", "Resources") + Os.DIRSEPERATOR;
		}
		#endregion
		private Func<T, string> orderBy;
		/// <summary>
		/// The latest change in the memory (currently loaded or altered XmlSettings) - no file access
		/// </summary>
		/// <returns></returns>
		public new DateTimeOffset GetMemChanged()
		{
			if (Infos == null)
				throw new FieldAccessException("Error in GetMemChanged(): Infos not initialized properly");
			return Infos.GetLastestItemChanged();
		}
		/// <summary>
		/// Opens the file and retrieves the youngest JsonItem in the file, identified by the JsonItem's modified attribute
		/// </summary>
		/// <remarks>the FlagFile attributes (i.e. LastWriteTimeUtc) are totally irrelevant for this result 
		/// - this only cares about the modified attribute of the JsonItem stored in the file</remarks>
		/// <returns>youngest JsonItem's modified</returns>
		public override DateTimeOffset GetFileChanged()
		{
			// TODO we might need a monitor or a semaphore here
			var diskItems = new JsonFile<T>(Name, readOnly: true);  // load a copy of the current file again to determine if is was changed on disk by Dropbox as a consequence of this file being written to disk on a different server
			return diskItems.GetMemChanged();   // what's the difference between this.GetMemChanged() and diskItems.GetMemChanged()? me (the file as it was when it was loaded from disk) and my other me (the version of the file that is now on disk) 
		}
		/// <summary>
		/// Merges the XmlSettings on disk into the XmlSettings in memory
		/// - the dirty flags are set accordingly - can be checked using Invalid()
		/// - in WriteThrough mode the file on disk will be updated if necessary and all dirty flags will be cleared 
		/// </summary>
		/// <param name="writeThrough">todo: describe writeThrough parameter on Merge</param>
		/// <remarks>deprecated - use Reload</remarks>
		public void Merge(bool writeThrough = false)
		{
			var currentDiskFile = new JsonFile<T>(Name);
			foreach (var item in currentDiskFile.Infos)
			{
				if (Infos.Contains(item.Name, true))
					Infos[item.Name].Merge(item);
				else Infos[item.Name] = item;
			}
			if (writeThrough)   // checked inside Store: && Infos.Invalid()
				Store();
		}
		/// <summary>
		/// A change file is a file that contains just one setting that can me merged into a file with many settings
		/// </summary>
		/// <param name="Item"></param>
		/// <param name="Machine">any machine name different from Environment.MachineName</param>
		/// <param name="MachineName">todo: describe MachineName parameter on CreateChangeFile</param>
		/// <remarks>
		/// new: 2014-11-24 one change file must be sufficient for all servers, Dropbox distributes it;
		/// make sure change file name contains the server who originated the change
		/// for this to make sense, the JsonFile (with all settings) has to be located inside a dropbox and it's path has to contain the Environment.MachineName
		/// old: D:\Dropbox\Config\3.3.3\U17138031\Servers.json with change file D:\Dropbox\Config\3.3.3\Titan562\Servers\U17138031_635284457032693173.json
		/// new: D:\Dropbox\Config\3.6\Users.json with change file D:\Dropbox\Config\Users\635284457032693173_U17138031.json
		/// </remarks>
		public void CreateChangeFile(T Item, string MachineName = null)
		{
			if (MachineName == null)
				MachineName = Environment.MachineName;
			var items = new List<T>();
			items.Add(Item);
			var changeFile = new RaiFile(ChangeDir + Item.Changed().UtcTicks.ToString() + "_" + MachineName + ".json");
			if (!File.Exists(changeFile.FullName))  // if the same file already exists it must contain the same change => no need to duplicate it
				new JsonFile<T>(changeFile.FullName, new JsonItems<T>(items), unflagged: true).Save();
		}
		public JsonItems<T> Infos
		{
			get { return infos; }
			set { infos = value; }
		}
		private JsonItems<T> infos;
		/// <summary>
		/// Directory that contains referenced files, i.e. Bargain.png for JsonFile&lt;OverlaySetting>
		/// </summary>
		public string ResourceDir
		{
			get
			{
				var file = new RaiFile(Name);
				return Os.NormSeperator(file.Path.Replace(Os.DIRSEPERATOR + Environment.MachineName, "") + typeof(T).Name.Replace("Item", "Resources") + Os.DIRSEPERATOR);
			}
		}
		/// <summary>
		/// Set modified if null; also executes Validate() for each setting
		/// </summary>
		private void MakeSureEachItemHasValidTimestamp()
		{
			foreach (var name in Infos.ItemNames)
			{
				if (Infos[name].Modified == null)
					Infos[name].Modified = DateTimeOffset.MinValue; // treat setting without modified as set at the beginning of time
			}
			// this only runs directly after Load() => every item is suposed to be !dirty
			foreach (var item in Infos)
				item.Validate();
		}
		/// <summary>
		/// Load Json file
		/// </summary>
		/// <param name="undercover">todo: describe undercover parameter on Load</param>
		public void Load(bool undercover = false)
		{
			{
				try // http://www.newtonsoft.com/json/help/html/SerializingJSON.htm
				{
					var s = File.ReadAllText(Name);
					if (string.IsNullOrEmpty(s))
						infos.Items = new JsonItems<T>().ToArray();
					else
					{
						#region Jil
						dynamic list1 = JArray.Parse(s);
						foreach (var elem in list1)
						{
							dynamic x = elem;
						}
						#endregion
						infos.Items = JsonConvert.DeserializeObject<List<T>>(s).ToArray();
					}
				}
				catch (InvalidOperationException)
				{
					throw;
				}
				finally
				{
					if (!(undercover || unflagged))
						ProcessFlag().Update(Infos.GetLastestItemChanged());
					Interlocked.Exchange(ref usingPersistence, 0);
				}
			}
		}
		/// <summary>
		/// Loads all changes and updates the file if necessary; run after Load
		/// </summary>
		/// <remarks>only the master is supposed to alter the main file or to delete change files; 
		/// new: change files now also have to be at least 10 min old to give all Dropbox instances a chance to do their sync
		/// and: only the master is allowed to perform deletion of ChangeFiles or updating of the main file on disk</remarks>
		public void MergeChanges()
		{
			var changes = new JsonItems<T>();
			#region collect changes
			JsonFile<T> jsonFile;
			if (Directory.Exists(ChangeDir))
			{
				foreach (var file in Directory.GetFiles(ChangeDir, "*.json").OrderByDescending(x => x)) // gets the latest first => less work if several changes exist
				{
					try
					{
						jsonFile = new JsonFile<T>(file, undercover: true);
						var cSet = jsonFile.Infos.First();
						if (changes.Contains(cSet.Name))
						{
							if (cSet.Changed() > changes[cSet.Name].Changed())
							{   // ok this one is newer, replace the one from before
								changes.Delete(cSet.Name);
								changes.Add(cSet, true);
							}
							// otherwise: just ignore this item - there is a newer one already
						}
						else changes.Add(jsonFile.Infos.First(), true); // as designed, change files only contain one item - TODO how to detect the same change in two different change files?
						if (RunningOnMaster() && (DateTimeOffset.UtcNow - (new System.IO.FileInfo(file)).CreationTime).TotalSeconds > 600)
							if (!ReadOnly)
								new RaiFile(file).rm(); // savely remove the file from the dropbox unless in ReadOnly mode
					}
					catch (System.InvalidOperationException)
					{
					}
				}
			}
			#endregion
			#region apply changes to 
			foreach (var changedItem in changes)
			{
				if (Infos.Contains(changedItem.Name, true))
				{   // Merge handles the dirty flag
					Infos[changedItem.Name].Merge(changedItem); // TODO double check modified and dirty flag
																			  //Infos[changedSetting.Name] = changedSetting;
				}
				else
				{   // new setting from disk means dirty flag has to be set, timestamp has to be preserved
					changedItem.Invalidate(preserveTimestamp: true);
					Infos[changedItem.Name] = changedItem;
				}
			}
			if (!ReadOnly)
				Store();    // changes the disk file if a change file existed with a newer version of a item or if i.e. a ram item was changed/deleted
			#endregion
		}
		/// <summary>
		/// Reloads an JsonFile and all it's changes from disk if necessary (changes this.Infos)
		/// </summary>
		/// <returns>true if a reload was performed (and Infos was changed), false otherwise (Infos unchanged)</returns>
		public bool Reload()
		{
			var masterUpdates = MasterUpdatesAvailable();
			var foreignChanges = ForeignChangesAvailable();
			//bool ownChanges = this.Infos.Invalid();
			if (masterUpdates && RunningOnMaster())
				throw new Exception("Some process changed the main file without permission => inconsistent data in " + this.Name + ", file " + this.Name);
			if (masterUpdates)
			{   // we need the new master; let's save our changes first
				Save(); // creates change files with changes that might loose if the master also has the same item changed
				Load(); // now we are guaranteed to have the latest stuff including all foreign changes
				return true;
			}
			if (!masterUpdates && foreignChanges)
			{ // just read the change files, not the main file; the items from the main file we already have in memory
				MergeChanges(); // whatever comes from disk is valid thereafter; a newer change from disk removes a older change in memory
									 //Save();	// no, doppelgemoppelt; MergeChanges performs a Save at the end if Invalid()
				return true;
			}
			return false;
		}
		/// <summary>makes File persistent - thread safe</summary>
		/// <param name="backup">backs up current xml file if true, or not if false, or uses the item passed in to the constructor if null</param>
		/// <param name="force">todo: describe force parameter on Save</param>
		/// <remarks>function varies across master and others: master can store to the actual file - others can only create change files
		/// </remarks>
		public void Save(bool? backup = null, bool force = false)
		{
			if (backup != null)
				Backup = (bool)backup;
			if (ReadOnly)
				throw new IOException("JsonFile " + Name + " was set to readonly mode but an attempt was made to execute JsonFile.Save");
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
		/// Save all changes as a single change file per setting
		/// </summary>
		/// <remarks>To determine what has changed, reloads the disk file's settings into a second variable and compares one by one with the ones in this.Infos.
		/// Writes a ChangeFile for a setting only if it is newer than the setting from disk. The time stamp of the change file has to be the modified time stamp in the setting.
		/// Does not care about settings that are newer on disk.</remarks>
		/// <seealso cref="Load"/>
		/// <seealso cref="MergeChanges"/>
		private void CreateChangeFiles()
		{
			var compareFile = new JsonFile<T>(Name, undercover: true);  // if undercover, Load does not update the time stamp in the flag file for the local server, ie RAPTOR133.info
			foreach (var memItem in Infos)
			{
				if (!compareFile.Infos.Contains(memItem.Name, withDeleted: true)                    // memSetting does not exist on disk
					|| (memItem.Changed() > compareFile.Infos[memItem.Name].Changed())) // memSetting is newer than diskSetting
					CreateChangeFile(memItem);
			}
		}
		/// <summary>makes JsonFile persistent; performs merge on item level</summary>
		/// <param name="force">todo: describe force parameter on Store</param>
		protected void Store(bool force = false)
		{   //use jil here
			if (Infos == null)
				return;
			var diskFile = new RaiFile(Name);
			#region only save if necessary
			if (force || Infos.Invalid())
			{
				if (ReadOnly)
					throw new IOException("JsonFile " + Name + " was set to readonly mode but an attempt was made to execute JsonFile.Store");
				Exception inner = null;
				if (Infos.Count > 0)    // passing in empty settings is not a valid way to delete the content of a settings file; silently refuse storing a new version
				{
					if (Backup)
						diskFile.backup();
					else diskFile.rm();
					new RaiFile(Name).mkdir();  // does nothing if dir was there; otherwise creates the dir and awaits materialization
					#region conditional execution for store typed JsonItems
					//var storeDynamics = false; // typeof(T).Name == nameof(DynamicItem);
					//if (storeDynamics)
					//{
					//	#region store dynamic objects - currently using Jil
					//	var items = descending ?
					//		(from item in Infos.ItemsAsObjects select item).OrderByDescending<dynamic, string>(x => x.Name) :
					//		(from item in Infos.ItemsAsObjects select item).OrderBy<dynamic, string>(x => x.Name);
					//	// TODO use OrderBy predicate
					//	var s = JSON.SerializeDynamic(items, jilOptions);
					//	File.WriteAllText(Name, s);
					//	#endregion
					//}
					//else
					//{
					#region store typed JsonItems
					#region old implementation using Newtonsoft
					JsonSerializer serializer = null;
					try
					{
						serializer = new JsonSerializer();
					}
					catch (Exception SerializerException)
					{
						inner = SerializerException.InnerException;
						// TODO that's not good; needs to write a error entry into log at least
						return;
					}
					#endregion
					var items = Infos.Items;
					IOrderedEnumerable<T> list = null;
					if (orderBy != null)
						list = descending ? items.OrderByDescending(orderBy) : items.OrderBy(orderBy);
					else list = descending ? items.OrderByDescending(x => x.Name) : items.OrderBy(x => x.Name);
					#region old implementation using Newtonsoft
					using (StreamWriter sw = new StreamWriter(Name))
					using (JsonWriter writer = new JsonTextWriter(sw))
					{
						serializer.Serialize(writer, items);
					}
					#endregion
					//var s = JSON.Serialize<IOrderedEnumerable<T>>(list, jilOptions);
					//	File.WriteAllText(Name, s);
					#endregion
					//}
					#endregion
					var changeTime = Infos.GetLastestItemChanged();
					File.SetLastWriteTimeUtc(Name, changeTime.DateTime);
					if (!unflagged)
					{
						var masterFlag = MasterFlag();
						masterFlag.Update(changeTime);      // set the new JsonFile timestamp to the flag file master.info
						ProcessFlag().Update(changeTime);   // make sure the server's flag file also has the date current
					}
					#region Validate each setting
					// 	means: this setting has been stored to disk; however, in a concurrent environment it does not mean, 
					// that the setting on disk still has the same value as the one in memory since it could have been changed 
					// elsewhere and synchronized back to this machine
					foreach (var item in Infos.Items)
						item.Validate();
					#endregion
				}
				else
				{
					throw new IOException("attempt to write a JsonFile with no items into "
					+ diskFile.FullName + "; the current memory representation should reflect the old file's items which it does not!", inner);
				}
			}
			#endregion
			//Infos.Validate();
		}
		/// <summary>Access a setting of the Infos via the setting's name</summary>
		/// <param name="itemName">indexer; value.name will be settingName after this</param>
		/// <remarks>acts as a business object; writing to external storage is initiated automatically if set to a new value
		///	you can write "xf["New Setting"] = oldSetting;" to create a copy of oldSetting with the new Name "New Setting"
		/// </remarks>
		/// <returns>object or default(T); compare to Infos[settingName]</returns>
		/// <exception cref="FieldAccessException"></exception>
		/// <value>the value can be null ... in fact, default(T) will be null for all Nullables; therefore, you cannot write File["123"].SomeProperty 
		/// since null.SomeProperty would throw an exception</value>
		public T this[string itemName]
		{
			get
			{
				if (!Infos.Contains(itemName, withDeleted: false))
					return default(T);
				return Infos[itemName];
			}
			/*private */
			set
			{
				if (string.IsNullOrEmpty(itemName))     // no-nonsense plausi check: make sure the used key is equal to Name
					throw new FieldAccessException("Constraint violation: indexer cannot be an empty string");
				Infos[itemName] = value;                        // sets modified there
				Infos[itemName].Name = itemName;    // nonsense correction constraint
			}
		}
		/// <summary>
		/// return a copy
		/// </summary>
		/// <param name="itemName"></param>
		/// <returns>a copy of the item stored in the container</returns>
		public T GetItem(string itemName)
		{
			if (!Infos.Contains(itemName))
				return default(T);
			return Infos.Get(itemName);
		}
		/// <summary>
		/// Factory method to eventually create an JsonFile of type T if changes are on disk
		/// </summary>
		/// <param name="fileName"></param>
		/// <param name="key"></param>
		/// <param name="subscriber"></param>
		/// <param name="orderBy"></param>
		/// <param name="descending"></param>
		/// <returns>null or an JsonFile of type T</returns>
		public static JsonFile<T> RefreshedFile(string fileName = null, string key = null, string subscriber = null, Func<T, string> orderBy = null, bool descending = false)
		{
			var vsf = new JsonFile<T>(fileName: fileName, key: key, subscriber: subscriber, orderBy: orderBy, descending: descending, autoload: false); // make sure the files is not loaded unneccesarily
			if (vsf.RunningOnMaster())
			{
				if (vsf.ForeignChangesAvailable())
				{
					vsf = new JsonFile<T>(fileName: fileName, key: key, subscriber: subscriber, orderBy: orderBy, descending: descending, autoload: true);
					return vsf;
				}
			}
			else
			{
				if (vsf.MasterUpdatesAvailable())
				{
					vsf = new JsonFile<T>(fileName: fileName, key: key, subscriber: subscriber, orderBy: orderBy, descending: descending, autoload: true);
					return vsf;
				}
			}
			return null;
		}
		/// <summary>Factory method to check for newer infos on disk</summary>
		/// <param name="values">todo: describe values parameter on LatestItems</param>
		/// <param name="fileName">todo: describe fileName parameter on LatestItems</param>
		/// <param name="key">todo: describe key parameter on LatestItems</param>
		/// <param name="subscriber">todo: describe subscriber parameter on LatestItems</param>
		/// <param name="orderBy">todo: describe orderBy parameter on LatestItems</param>
		/// <param name="descending">todo: describe descending parameter on LatestItems</param>
		/// <returns>the passed in values if nothing newer is available on disk; the latest item from the disk otherwise</returns>
		public static JsonItems<T> LatestItems(object values, string fileName = null, string key = null, string subscriber = null, Func<T, string> orderBy = null, bool descending = false)
		{
			var refreshed = JsonFile<T>.RefreshedFile(fileName, key, subscriber, orderBy, descending);
			if (refreshed != null)
				return refreshed.Infos;
			return (JsonItems<T>)values;
		}
		/// <summary>constructor</summary>
		/// <param name="fileName">save/load location, full name with path; standard file name and path if omitted; standard file name will be appended if just path</param>
		/// <param name="values">values that already exist in memory; will be merged with existing items</param>
		/// <param name="key"></param>
		/// <param name="orderBy">if skipped, x => x.Name</param>
		/// <param name="descending"></param>
		/// <param name="undercover"> if undercover, Load does not update the time stamp in the flag file for the local server, ie RAPTOR133.info; also: does not pick up the change files</param>
		/// <param name="unflagged">do not write flag file ie master.info, RAPTOR133.info, ...</param>
		public JsonFile(string fileName = null, JsonItems<T> values = null, string key = null, string subscriber = null, Func<T, string> orderBy = null, bool descending = false,
			bool readOnly = true, bool backup = false, bool undercover = false, bool unflagged = false, bool autoload = true, bool ignoreCase = false)
			: base(readOnly, backup, unflagged, descending)
		{
			this.orderBy = orderBy ?? new Func<T, string>(x => x.Name);
			this.descending = descending;
			Name = Os.NormSeperator(fileName ?? defaultFileName(subscriber, ""));
			if (Name.EndsWith(Os.DIRSEPERATOR))
			{
				var f = new RaiFile(defaultFileName(subscriber, ""));
				f.Path = Name;
				Name = f.FullName;
			}
			infos = new JsonItems<T>((subscriber != null ? subscriber + "." : "") + (key ?? new RaiFile(Name).Name), ignoreCase);
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
				).ToList(); // ToList should be creating a copy and not just keep a reference => check it out in the debugger!!!
			#endregion
			if (autoload)   // new option autoload: false reduces file io if Reload is not necessary
			{
				if (File.Exists(Name))
					Load(undercover);   // does not pick up the change files
				MergeChanges(); // this one does, calls Store()
				#region replaces Merge()
				if (memoryChanges != null)
				{
					foreach (var item in memoryChanges)
					{
						if (Infos.Contains(item.Name, true))
							Infos[item.Name].Merge(item);   // Merge() handles the dirty flag
						else
						{
							Infos[item.Name] = item;
							Infos[item.Name].Invalidate(preserveTimestamp: true);       // new in mem => dirty
						}
					}
					if (Infos.Invalid())    // can happen if the loop above found newer items in values
						Save(); // writes main file or changeFiles
				}
				#endregion
			}
		}
		/// <summary>
		/// defaultFileName with or without subscriber, with or without version
		/// </summary>
		/// <param name="subscriber"></param>
		/// <param name="version">"" => get version from JsonPit module; null => no version in path</param>
		/// <returns></returns>
		public static string defaultFileName(string subscriber, string version = null)
		{
			if (version != null && version.Length == 0)
				version = Version;
			var file = new RaiFile(typeof(T).Name.Replace("Item", "s") + ".json") { Path = JsonFile<T>.ConfigDirDefault };
			if (!string.IsNullOrEmpty(version))
				file.Path += version + Os.DIRSEPERATOR;
			if (!string.IsNullOrWhiteSpace(subscriber))
				file.Path += subscriber + Os.DIRSEPERATOR;
			return file.FullName;
		}
	}
	/// <summary>Structure that keeps all item data</summary>
	public class JsonItems<T> : ItemsBase, IEnumerable<T>
		where T : Item, new()
	{
		#region with or without ignoreCase
		private StringComparer Comparer
		{
			get
			{
				return ignoreCase ? StringComparer.InvariantCultureIgnoreCase : StringComparer.InvariantCulture;
			}
		}
		private StringComparison Comparison
		{
			get
			{
				return ignoreCase ? StringComparison.InvariantCultureIgnoreCase : StringComparison.InvariantCulture;
			}
		}
		public void ConsiderCase()
		{
			if (ignoreCase)
			{
				ignoreCase = false;
				if (items != null)
					items = new ConcurrentDictionary<string, T>(items, ignoreCase ? StringComparer.InvariantCulture : StringComparer.InvariantCultureIgnoreCase);
			}
		}
		public void IgnoreCase()
		{
			if (!ignoreCase)
			{
				ignoreCase = true;
				if (items != null)
					items = new ConcurrentDictionary<string, T>(items, ignoreCase ? StringComparer.InvariantCulture : StringComparer.InvariantCultureIgnoreCase);
			}
		}
		private bool ignoreCase = false;
		#endregion
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
			var query = from item in Items where !item.Valid() select item.Name;
			return query.Count() > 0;
		}
		/// <summary>
		/// latest change - does not consider the surrounding container, just the settings
		/// </summary>
		/// <returns>timestamp, DateTimeOffset.MinValue of none</returns>
		public DateTimeOffset GetLastestItemChanged()
		{
			var list = (from _ in Items select _.Changed());
			list = list.OrderByDescending(x => x);
			return list.Count() > 0 ? list.First() : DateTimeOffset.MinValue;
		}
		/// <summary>
		/// find a template setting in tList
		/// </summary>
		/// <param name="itemName">name of template</param>
		/// <param name="defaultTemplate">tries to find this one if template could not be found; 
		///		if null: create one using the standard constructor and also set its modified to DateTimeOffset.MinValue</param>
		/// <returns>the templateSetting with the given name if found, or the default setting or an empty setting</returns>
		/// <remarks>always returns a setting; does not throw an exception</remarks>
		public T findItem(string itemName, string defaultTemplate = null)
		{
			T defTmp = null, t = null;
			foreach (KeyValuePair<string, T> item in items)
			{
				if (item.Key.Equals(itemName, Comparison))
				{
					t = item.Value; // new SettingClass(setting);
					break;
				}
				if (!string.IsNullOrEmpty(defaultTemplate) && item.Key.Equals(defaultTemplate, Comparison))
					defTmp = item.Value; // new SettingClass(setting);
			}
			if (t != null)
				return t;
			if (defTmp != null)
				return defTmp;
			var newT = new T { Modified = DateTimeOffset.MinValue };
			return newT;
		}
		internal ConcurrentDictionary<string, T> items = new ConcurrentDictionary<string, T>();
		/// <summary>property; deleted items count</summary>
		public int Count
		{
			get { return items == null ? 0 : items.Count; }
		}
		/// <summary>method; does not count deleted items</summary>
		/// <param name="countDeletedItems">todo: describe countDeletedItems parameter on Length</param>
		public int Length(bool countDeletedItems = false)
		{
			return
				(from item in items
				 where countDeletedItems || !item.Value.Deleted
				 select item).Count();
		}
		/// <summary>add one setting at the end</summary>
		/// <param name="item"></param>
		/// <param name="preserve">set true, if you want to add a new or recovered setting and preserve the item's deleted and modified</param>
		public void Add(T item, bool preserve = false)
		{
			if (!preserve)
			{
				item.Deleted = false;
				item.Modified = DateTimeOffset.UtcNow;
			}
			//if (items == null)
			//	items = new ConcurrentDictionary<string, T>(Comparer);
			items[item.Name] = item;
			//Invalidate();
		}
		/// <summary>
		/// create a copy of the item
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
			var copy = (T)this[name].Clone();
			copy.Name = name + copyNumber.ToString();
			copy.Invalidate();
			Add(copy);
			return copy.Name;
		}
		/// <summary>delete one item</summary>
		/// <param name="item"></param>
		public bool Remove(T item)
		{
			T value;
			var result = items.TryRemove(item.Name, out value);
			//if (result)
			//	Invalidate();
			return result;
		}
		/// <summary>logical delete; sets deleted flag</summary>
		/// <param name="itemName"></param>
		/// <param name="by">who deleted this item; used to write by into the item's Note</param>
		/// <param name="backDate">todo: describe backDate parameter on Delete</param>
		/// <returns>true: did not exists or is marked as deleted now</returns>
		public bool Delete(string itemName, string by = null, bool backDate = true)
		{
			if (string.IsNullOrEmpty(itemName))
				return true;
			try
			{
				items[itemName].Delete(by, backDate);   // will not delete a deleted item again (conserves timestamp and note)
			}
			catch (KeyNotFoundException) { }
			catch (Exception)
			{
				return false;
			}
			return true;
		}
		/// <summary>
		/// Update means the item has changed or is treated as if it has
		/// </summary>
		/// <param name="oldName"></param>
		/// <param name="newItem"></param>
		/// <param name="by"></param>
		/// <returns></returns>
		public bool Update(string oldName, T newItem, string by = null)
		{
			newItem.Invalidate();
			if (oldName == newItem.Name)
			{
				newItem.Invalidate();
				this[oldName] = newItem;
				return true;
			}
			if (this.Contains(newItem.Name))
				return false;   // cannot change the name to an already existing name (name is key and must be unique)
			#region key has changed - delete oldSetting first
			this.Delete(oldName, by, backDate: true);
			newItem.Invalidate();
			Add(newItem);
			#endregion
			return true;
		}
		/// <summary>Get or set whole List; list contains all elements including deleted ones</summary>
		/// <remarks>use SettingNames or Select to get a list without deleted entries</remarks>
		//[XmlElement("XmlSettings")]
		public T[] Items
		{
			get
			{
				//if (items == null)
				//	items = new ConcurrentDictionary<string, T>(Comparer);
				_collection = new List<T>(from _ in items select _.Value);
				return _collection.ToArray();
			}
			set
			{
				items = new ConcurrentDictionary<string, T>(Comparer);
				if (value != null)
				{
					#region remove duplicates in value manually - the last one wins
					foreach (var kvp in value)
						if (items.ContainsKey(kvp.Name))
						{
							var x = items[kvp.Name];
							if (x.Modified > kvp.Modified)
							{
								x.Note += "\n{ duplicate entry with modification time " + kvp.Modified.ToString("o") + " removed }";
								items[kvp.Name] = x;
							}
							else
							{
								kvp.Note += "\n{ duplicate entry with modification time " + x.Modified.ToString("o") + " removed }";
								items[kvp.Name] = kvp;
							}
						}
						else items[kvp.Name] = kvp;
					#endregion
				}
			}
		}
		private List<T> _collection = new List<T>();    // TODO check if this is fresh when it needs to be
		#region dynamic TODO: move to PersistDynamic
		/// <summary>
		/// creates a copy - useable for serialization with Jil
		/// </summary>
		public List<dynamic> ItemsAsObjects
		{
			get
			{
				return new List<dynamic>(from _ in items select _.Value.Clone());
			}
		}
		#endregion
		#region IEnumerable implementation
		/// <summary>creates a List of the Value entries in the Dictionary</summary>
		/// <remarks>be aware that this method creates a new list everytime it is called; 
		/// only use it when you need the values in a List - otherwise access settings;</remarks>
		/// <example>use this.Count instead of this.Count() since it consumes less ressources</example>
		/// <returns></returns>
		public IEnumerator<T> GetEnumerator()
		{
			_collection = items == null ? new List<T>() : new List<T>(from _ in items select _.Value);
			return _collection.GetEnumerator();
		}
		IEnumerator IEnumerable.GetEnumerator()
		{
			return GetEnumerator();
		}
		#endregion
		/// <summary>Access a setting via the setting's name</summary>
		/// <param name="itemName">name</param>
		/// <remarks>acts as a business object; writing to external storage is initiated automatically if set to a new value</remarks>
		/// <returns>object or throws KeyNotFoundException</returns>
		public T this[string itemName]
		{
			get
			{
				return items[itemName];
			}
			set
			{
				if (string.IsNullOrWhiteSpace(value.Name))
					value.Name = itemName;
				//if (items == null)
				//	items = new ConcurrentDictionary<string, T>(Comparer);
				items[itemName] = value;
				//Invalidate();	// changes the timestamp in the container - does not affect the setting
				if (value.Changed() != items[itemName].Changed())
					throw new InvalidDataException("Error: inserting/updating a setting into a container is not supposed to change the setting's timestamp");
			}
		}
		/// <summary>
		/// Access a setting
		/// </summary>
		/// <param name="itemName">index</param>
		/// <returns>a cloned copy of the setting stored in the collection</returns>
		public T Get(string itemName)
		{
			//if (items == null)
			//	items = new ConcurrentDictionary<string, T>(Comparer);
			return (T)items[itemName].Clone();
		}
		public bool Contains(string itemName, bool withDeleted = false)
		{
			#region Testcase TestProfileMerge seems to need this
			//if (items == null)
			//	items = new ConcurrentDictionary<string, T>(Comparer);
			#endregion
			var isThere = items.Keys.Contains(itemName, Comparer); // settings.ContainsKey(settingName);
			if (withDeleted)
				return isThere;
			return isThere && !items[itemName].Deleted;
		}
		/// <summary>
		/// lists the names of all settings that are not deleted
		/// </summary>
		public string[] ItemNames
		{
			get { return (from _ in items where _.Value.Deleted != true select _.Value.Name).ToArray(); }
		}
		/// <summary>select settings indicated by passed-in names</summary>
		/// <param name="itemNames">identifying names</param>
		/// <remarks>throws an exception if one of the requested settings does not exist</remarks>
		/// <returns>array</returns>
		public T[] Select(string[] itemNames)
		{
			var list = new List<T>();
			T elem;
			foreach (string oName in itemNames)
			{
				elem = this[oName];
				if (!elem.Deleted)
					list.Add(this[oName]);
			}
			return list.ToArray();
		}
		/// <summary>Selects all items that are not deleted and contain the filter</summary>
		/// <param name="filter">string that can contain wildcard and field identifiers</param>
		/// <param name="comp">todo: describe comp parameter on Select</param>
		/// <returns>array</returns>
		public T[] Select(string filter, Compare comp = Compare.ByProperty)
		{
			return (from item in items
					  where !item.Value.Deleted && item.Value.Matches(filter, comp)
					  select item.Value).ToArray();
		}
		public T[] Select(string filter, int pageIndex, int pageSize, out int totalRecords)
		{
			// TODO OrderBy(x => x.name) or better 
			var all = (from item in items
						  where !item.Value.Deleted && item.Value.Matches(filter)
						  select item.Value);
			totalRecords = all.Count();
			return all.ToArray();
			// TODO Skip(pageIndex * pageSize).Take(pageSize).
			// TODO check if pageIndex runs from 0 or from 1
		}
		/// <summary>Copy constructor</summary>
		public JsonItems(JsonItems<T> from)
			: this(from.key)
		{
			//dirty = false;
			ignoreCase = from.ignoreCase;
			Items = from.Items; // deep copy since from.Settings creates an Array
		}
		public JsonItems(IEnumerable<T> items, string key = null, bool ignoreCase = false)
			: this(key)
		{
			this.ignoreCase = ignoreCase;
			Items = null;
			foreach (var x in items)
				this.items[x.Name] = x;
		}
		/// <summary>Constructor</summary>
		public JsonItems(string key, bool ignoreCase = false)
			: base(key)
		{
			this.ignoreCase = ignoreCase;
			Items = null;
		}
		public JsonItems()
		{
		}
	}
}

