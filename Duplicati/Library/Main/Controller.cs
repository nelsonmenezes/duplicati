#region Disclaimer / License
// Copyright (C) 2011, Kenneth Skovhede
// http://www.hexad.dk, opensource@hexad.dk
// 
// This library is free software; you can redistribute it and/or
// modify it under the terms of the GNU Lesser General Public
// License as published by the Free Software Foundation; either
// version 2.1 of the License, or (at your option) any later version.
// 
// This library is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
// Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public
// License along with this library; if not, write to the Free Software
// Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
// 
#endregion
using System;
using System.Collections.Generic;
using System.Text;
using Duplicati.Library.Utility;

namespace Duplicati.Library.Main
{
    public class Controller : IDisposable
    {
        /// <summary>
        /// The backend url
        /// </summary>
        private string m_backend;
        /// <summary>
        /// The parsed type-safe version of the commandline options
        /// </summary>
        private Options m_options;
        /// <summary>
        /// The destination for all output messages during execution
        /// </summary>
        private IMessageSink m_messageSink;

        /// <summary>
        /// A flag indicating if logging has been set, used to dispose the logging
        /// </summary>
        private bool m_hasSetLogging = false;
        
        /// <summary>
        /// The current executing task
        /// </summary>
        private ITaskControl m_currentTask = null;
        
        /// <summary>
        /// The thread running the current task
        /// </summary>
        private System.Threading.Thread m_currentTaskThread = null;

        /// <summary>
        /// This gets called whenever execution of an operation is started or stopped; it currently handles the AllowSleep option
        /// </summary>
        /// <param name="isRunning">Flag indicating execution state</param>
        private void OperationRunning(bool isRunning)
        {          
            if (m_options != null && !m_options.AllowSleep && !Duplicati.Library.Utility.Utility.IsClientLinux)
                try
                {
                    Win32.SetThreadExecutionState(Win32.EXECUTION_STATE.ES_CONTINUOUS | (isRunning ? Win32.EXECUTION_STATE.ES_SYSTEM_REQUIRED : 0));
                }
                catch { } //TODO: Report this somehow
        }

        /// <summary>
        /// Constructs a new interface for performing backup and restore operations
        /// </summary>
        /// <param name="backend">The url for the backend to use</param>
        /// <param name="options">All required options</param>
        public Controller(string backend, Dictionary<string, string> options, IMessageSink messageSink)
        {
            m_backend = backend;
            m_options = new Options(options);
            m_messageSink = messageSink;
        }

        public Duplicati.Library.Interface.IBackupResults Backup(string[] inputsources, IFilter filter = null)
		{
            return RunAction(new BackupResults(), ref inputsources, (result) => {
            
				if (inputsources == null || inputsources.Length == 0)
					throw new Exception(Strings.Controller.NoSourceFoldersError);

                var sources = new List<string>(inputsources);

				//Make sure they all have the same format and exist
				for(int i = 0; i < sources.Count; i++)
				{
					try
					{
						sources[i] = System.IO.Path.GetFullPath(sources[i]);
					}
					catch (Exception ex)
					{
						throw new ArgumentException(string.Format(Strings.Controller.InvalidPathError, sources[i], ex.Message), ex);
					}
                	
                    var fi = new System.IO.FileInfo(sources[i]);
                    var di = new System.IO.DirectoryInfo(sources[i]);
                    if (!(fi.Exists || di.Exists) && !m_options.AllowMissingSource)
                        throw new System.IO.IOException(String.Format(Strings.Controller.SourceIsMissingError, sources[i]));
                    
                    if (!fi.Exists)
    					sources[i] = Library.Utility.Utility.AppendDirSeparator(sources[i]);
				}

				//Sanity check for duplicate folders and multiple inclusions of the same folder
				for(int i = 0; i < sources.Count - 1; i++)
				{
					for(int j = i + 1; j < sources.Count; j++)
						if (sources[i].Equals(sources[j], Library.Utility.Utility.IsFSCaseSensitive ? StringComparison.CurrentCulture : StringComparison.CurrentCultureIgnoreCase))
                        {
                            result.AddVerboseMessage("Removing duplicate source: {0}", sources[j]);
							sources.RemoveAt(j);
                            j--;
                        }
						else if (sources[i].StartsWith(sources[j], Library.Utility.Utility.IsFSCaseSensitive ? StringComparison.CurrentCulture : StringComparison.CurrentCultureIgnoreCase))
                        {
                            result.AddVerboseMessage("Removing source \"{0}\" because it is a subfolder of \"{1}\"", sources[i], sources[j]);
                            filter = Library.Utility.JoinedFilterExpression.Join(new FilterExpression(sources[i]), filter);
                            
                            sources.RemoveAt(i);
                            i--;
                            break;
                        }
				}

                using(var h = new Operation.BackupHandler(m_backend, m_options, result))
                    h.Run(sources.ToArray(), filter);
            });
        }

        public Library.Interface.IRestoreResults Restore(string[] paths, Library.Utility.IFilter filter = null)
		{
            return RunAction(new RestoreResults(), ref paths, (result) => {
    			new Operation.RestoreHandler(m_backend, m_options, result).Run(paths, filter);
            });
        }

        public Duplicati.Library.Interface.IRestoreControlFilesResults RestoreControlFiles(IEnumerable<string> files = null, Library.Utility.IFilter filter = null)
        {
            return RunAction(new RestoreControlFilesResults(), (result) => {
                new Operation.RestoreControlFilesHandler(m_backend, m_options, result).Run(files, filter);
            });
        }

        public Duplicati.Library.Interface.IDeleteResults Delete()
		{
            return RunAction(new DeleteResults(), (result) => {
    			new Operation.DeleteHandler(m_backend, m_options, result).Run();
            });
        }

        public Duplicati.Library.Interface.IRepairResults Repair()
        {
            return RunAction(new RepairResults(), (result) => {
                new Operation.RepairHandler(m_backend, m_options, result).Run();
            });
        }
        
        public Duplicati.Library.Interface.IListResults List(Library.Utility.IFilter filter = null)
        {
            return List((IEnumerable<string>)null, filter);
        }

        public Duplicati.Library.Interface.IListResults List (string filterstring, Library.Utility.IFilter filter = null)
        {
            return List(filterstring == null ? null : new string[] { filterstring }, null);
        }
        
        public Duplicati.Library.Interface.IListResults List(IEnumerable<string> filterstrings, Library.Utility.IFilter filter = null)
		{
            return RunAction(new ListResults(), (result) => {
    			new Operation.ListFilesHandler(m_backend, m_options, result).Run(filterstrings, filter);
            });
        }
        
        public Duplicati.Library.Interface.IListResults ListControlFiles(IEnumerable<string> filterstrings = null, Library.Utility.IFilter filter = null)
        {
            return RunAction(new ListResults(), (result) => {
                new Operation.ListControlFilesHandler(m_backend, m_options, result).Run(filterstrings, filter);
            });
        }
        
        public Duplicati.Library.Interface.ICompactResults Compact()
        {
            return RunAction(new CompactResults(), (result) => {
                new Operation.CompactHandler(m_backend, m_options, result).Run();
            });
        }
        
        public Duplicati.Library.Interface.IRecreateDatabaseResults RecreateDatabase(string targetpath)
        {
            var t = new string[] { string.IsNullOrEmpty(targetpath) ? m_options.Dbpath : targetpath };
            
            return RunAction(new RecreateDatabaseResults(), ref t, (result) => {
                using(var h = new Operation.RecreateDatabaseHandler(m_backend, m_options, result))
                    h.Run(t[0]);
            });
        }

        public Duplicati.Library.Interface.ICreateLogDatabaseResults CreateLogDatabase(string targetpath)
        {
            var t = new string[] { targetpath };
            
            return RunAction(new CreateLogDatabaseResults(), ref t, (result) => {
                result.TargetPath = t[0];
                new Operation.CreateBugReportHandler(t[0], m_options, result).Run();
            });
        }

        public Duplicati.Library.Interface.IListChangesResults ListChanges(string baseVersion, string targetVersion, IEnumerable<string> filterstrings = null, Library.Utility.IFilter filter = null)
        {
            var t = new string[] { baseVersion, targetVersion };
            
            return RunAction(new ListChangesResults(), ref t, (result) => {
                new Operation.ListChangesHandler(m_backend, m_options, result).Run(t[0], t[1], filterstrings, filter);
            });
        }

        public Duplicati.Library.Interface.ITestResults Test(long samples = 1)
        {            
            return RunAction(new TestResults(), (result) => {
                new Operation.TestHandler(m_backend, m_options, result).Run(samples);
            });
        }
        
        public Library.Interface.ITestFilterResults TestFilter(string[] paths, Library.Utility.IFilter filter = null)
        {
            m_options.RawOptions["verbose"] = "true";
            m_options.RawOptions["dry-run"] = "true";
            m_options.RawOptions["dbpath"] = "INVALID!";
            
            return RunAction(new TestFilterResults(), ref paths, (result) => {
                new Operation.TestFilterHandler(m_options, result).Run(paths, filter);
            });
        }
        
        private T RunAction<T>(T result, Action<T> method)
            where T : ISetCommonOptions, ITaskControl
        {
            var tmp = new string[0];
            return RunAction<T>(result, ref tmp, method);
        }
                
        private T RunAction<T>(T result, ref string[] paths, Action<T> method)
            where T : ISetCommonOptions, ITaskControl
        {
            try
            {
                m_currentTask = result;
                m_currentTaskThread = System.Threading.Thread.CurrentThread;
                using(new Logging.Timer(string.Format("Running {0} took", result.MainOperation)))
                {
                    SetupCommonOptions(result, ref paths);
                    OperationRunning(true);
    				
                    method(result);
                    
                    result.EndTime = DateTime.UtcNow;
                    result.SetDatabase(null);
    
                    OnOperationComplete(result);
                    return result;
                }
            }
            catch (Exception ex)
            {
                Logging.Log.WriteMessage("Terminated with error: " + ex.Message, Duplicati.Library.Logging.LogMessageType.Error, ex);
                                
                OnOperationComplete(ex);
                
                try { (result as BasicResults).OperationProgressUpdater.UpdatePhase(OperationPhase.Error); }
                catch { }
                
                throw;
            }
            finally
            {
                m_currentTask = null;
                m_currentTaskThread = null;
            }
		}
		
		private void OnOperationComplete(object result)
		{
            if (m_options != null && m_options.LoadedModules != null)
            {
                foreach (KeyValuePair<bool, Library.Interface.IGenericModule> mx in m_options.LoadedModules)
                    if (mx.Key && mx.Value is Duplicati.Library.Interface.IGenericCallbackModule)
                        try { ((Duplicati.Library.Interface.IGenericCallbackModule)mx.Value).OnFinish(result); }
                        catch (Exception ex) { Logging.Log.WriteMessage(string.Format("OnFinish callback {0} failed: {1}", mx.Key, ex.Message), Duplicati.Library.Logging.LogMessageType.Warning, ex); }

                foreach (KeyValuePair<bool, Library.Interface.IGenericModule> mx in m_options.LoadedModules)
                    if (mx.Key && mx.Value is IDisposable)
                        try { ((IDisposable)mx.Value).Dispose(); }
                        catch (Exception ex) { Logging.Log.WriteMessage(string.Format("Dispose for {0} failed: {1}", mx.Key, ex.Message), Duplicati.Library.Logging.LogMessageType.Warning, ex); }

                m_options.LoadedModules.Clear();
                OperationRunning(false);
            }

            if (m_hasSetLogging && Logging.Log.CurrentLog is Logging.StreamLog)
            {
                Logging.StreamLog sl = (Logging.StreamLog)Logging.Log.CurrentLog;
                Logging.Log.CurrentLog = null;
                sl.Dispose();
                m_hasSetLogging = false;
            }		
		}

        private void SetupCommonOptions(ISetCommonOptions result, ref string[] paths)
        {
            m_options.MainAction = result.MainOperation;
            result.MessageSink = m_messageSink;
            
            switch (m_options.MainAction)
            {
                case OperationMode.Backup:
                    break;
                
                default:
                    //It only makes sense to enable auto-creation if we are writing files.
                    if (!m_options.RawOptions.ContainsKey("disable-autocreate-folder"))
                        m_options.RawOptions["disable-autocreate-folder"] = "true";
                    break;
            }

            //Load all generic modules
            m_options.LoadedModules.Clear();

            foreach (Library.Interface.IGenericModule m in DynamicLoader.GenericLoader.Modules)
                m_options.LoadedModules.Add(new KeyValuePair<bool, Library.Interface.IGenericModule>(Array.IndexOf<string>(m_options.DisableModules, m.Key.ToLower()) < 0 && (m.LoadAsDefault || Array.IndexOf<string>(m_options.EnableModules, m.Key.ToLower()) >= 0), m));

            foreach (KeyValuePair<bool, Library.Interface.IGenericModule> mx in m_options.LoadedModules)
                if (mx.Key)
                {
                    mx.Value.Configure(m_options.RawOptions);
                    if (mx.Value is Library.Interface.IGenericCallbackModule)
                        ((Library.Interface.IGenericCallbackModule)mx.Value).OnStart(result.MainOperation.ToString(), ref m_backend, ref paths);
                }

            OperationRunning(true);

            Library.Logging.Log.LogLevel = m_options.Loglevel;
            if (!string.IsNullOrEmpty(m_options.Logfile))
            {
                m_hasSetLogging = true;
                var path = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(m_options.Logfile));
                if (!System.IO.Directory.Exists(path))
                    System.IO.Directory.CreateDirectory(path);
                Library.Logging.Log.CurrentLog = new Library.Logging.StreamLog(m_options.Logfile);
            }

            result.VerboseErrors = m_options.DebugOutput;
            result.VerboseOutput = m_options.Verbose;

            if (m_options.HasTempDir)
                Library.Utility.TempFolder.SystemTempPath = m_options.TempDir;

            if (!string.IsNullOrEmpty(m_options.ThreadPriority))
                System.Threading.Thread.CurrentThread.Priority = Library.Utility.Utility.ParsePriority(m_options.ThreadPriority);

            if (string.IsNullOrEmpty(m_options.Dbpath))
                m_options.Dbpath = DatabaseLocator.GetDatabasePath(m_backend, m_options);

            ValidateOptions(result);

            Library.Logging.Log.WriteMessage(string.Format(Strings.Controller.StartingOperationMessage, m_options.MainAction), Logging.LogMessageType.Information);
        }

        /// <summary>
        /// This function will examine all options passed on the commandline, and test for unsupported or deprecated values.
        /// Any errors will be logged into the statistics module.
        /// </summary>
        /// <param name="options">The commandline options given</param>
        /// <param name="backend">The backend url</param>
        /// <param name="stats">The statistics into which warnings are written</param>
        private void ValidateOptions(ILogWriter log)
        {
            //No point in going through with this if we can't report
            if (log == null)
                return;

            //Keep a list of all supplied options
            Dictionary<string, string> ropts = new Dictionary<string, string>(m_options.RawOptions);
            
            //Keep a list of all supported options
            Dictionary<string, Library.Interface.ICommandLineArgument> supportedOptions = new Dictionary<string, Library.Interface.ICommandLineArgument>();

            //There are a few internal options that are not accessible from outside, and thus not listed
            foreach (string s in Options.InternalOptions)
                supportedOptions[s] = null;

            //Figure out what module options are supported in the current setup
            List<Library.Interface.ICommandLineArgument> moduleOptions = new List<Duplicati.Library.Interface.ICommandLineArgument>();
            Dictionary<string, string> disabledModuleOptions = new Dictionary<string, string>();

            foreach (KeyValuePair<bool, Library.Interface.IGenericModule> m in m_options.LoadedModules)
                if (m.Value.SupportedCommands != null)
                    if (m.Key)
                        moduleOptions.AddRange(m.Value.SupportedCommands);
                    else
                    {
                        foreach (Library.Interface.ICommandLineArgument c in m.Value.SupportedCommands)
                        {
                            disabledModuleOptions[c.Name] = m.Value.DisplayName + " (" + m.Value.Key + ")";

                            if (c.Aliases != null)
                                foreach (string s in c.Aliases)
                                    disabledModuleOptions[s] = disabledModuleOptions[c.Name];
                        }
                    }
            
            // Throw url-encoded options into the mix
            //TODO: This can hide values if both commandline and url-parameters supply the same key
            var ext = new Library.Utility.Uri(m_backend).QueryParameters;
            foreach(var k in ext.AllKeys)
                ropts[k] = ext[k];

            //Now run through all supported options, and look for deprecated options
            foreach (IList<Library.Interface.ICommandLineArgument> l in new IList<Library.Interface.ICommandLineArgument>[] { 
                m_options.SupportedCommands, 
                DynamicLoader.BackendLoader.GetSupportedCommands(m_backend), 
                m_options.NoEncryption ? null : DynamicLoader.EncryptionLoader.GetSupportedCommands(m_options.EncryptionModule),
                moduleOptions,
                DynamicLoader.CompressionLoader.GetSupportedCommands(m_options.CompressionModule) })
            {
                if (l != null)
                    foreach (Library.Interface.ICommandLineArgument a in l)
                    {
                        if (supportedOptions.ContainsKey(a.Name) && Array.IndexOf(Options.KnownDuplicates, a.Name.ToLower()) < 0)
                            log.AddWarning(string.Format(Strings.Controller.DuplicateOptionNameWarning, a.Name), null);

                        supportedOptions[a.Name] = a;

                        if (a.Aliases != null)
                            foreach (string s in a.Aliases)
                            {
                                if (supportedOptions.ContainsKey(s) && Array.IndexOf(Options.KnownDuplicates, s.ToLower()) < 0)
                                    log.AddWarning(string.Format(Strings.Controller.DuplicateOptionNameWarning, s), null);

                                supportedOptions[s] = a;
                            }

                        if (a.Deprecated)
                        {
                            List<string> aliases = new List<string>();
                            aliases.Add(a.Name);
                            if (a.Aliases != null)
                                aliases.AddRange(a.Aliases);

                            foreach (string s in aliases)
                                if (ropts.ContainsKey(s))
                                {
                                    string optname = a.Name;
                                    if (a.Name != s)
                                        optname += " (" + s + ")";

                                    log.AddWarning(string.Format(Strings.Controller.DeprecatedOptionUsedWarning, optname, a.DeprecationMessage), null);
                                }

                        }
                    }
            }

            //Now look for options that were supplied but not supported
            foreach (string s in ropts.Keys)
                if (!supportedOptions.ContainsKey(s))
                    if (disabledModuleOptions.ContainsKey(s))
                        log.AddWarning(string.Format(Strings.Controller.UnsupportedOptionDisabledModuleWarning, s, disabledModuleOptions[s]), null);
                    else
                        log.AddWarning(string.Format(Strings.Controller.UnsupportedOptionWarning, s), null);

            //Look at the value supplied for each argument and see if is valid according to its type
            foreach (string s in ropts.Keys)
            {
                Library.Interface.ICommandLineArgument arg;
                if (supportedOptions.TryGetValue(s, out arg) && arg != null)
                {
                    string validationMessage = ValidateOptionValue(arg, s, ropts[s]);
                    if (validationMessage != null)
                        log.AddWarning(validationMessage, null);
                }
            }

            //TODO: Based on the action, see if all options are relevant
        }

        /// <summary>
        /// Checks if the value passed to an option is actually valid.
        /// </summary>
        /// <param name="arg">The argument being validated</param>
        /// <param name="optionname">The name of the option to validate</param>
        /// <param name="value">The value to check</param>
        /// <returns>Null if no errors are found, an error message otherwise</returns>
        public static string ValidateOptionValue(Library.Interface.ICommandLineArgument arg, string optionname, string value)
        {
            if (arg.Type == Duplicati.Library.Interface.CommandLineArgument.ArgumentType.Enumeration)
            {
                bool found = false;
                foreach (string v in arg.ValidValues ?? new string[0])
                    if (string.Equals(v, value, StringComparison.CurrentCultureIgnoreCase))
                    {
                        found = true;
                        break;
                    }

                if (!found)
                    return string.Format(Strings.Controller.UnsupportedEnumerationValue, optionname, value, string.Join(", ", arg.ValidValues ?? new string[0]));

            }
            else if (arg.Type == Duplicati.Library.Interface.CommandLineArgument.ArgumentType.Boolean)
            {
                if (!string.IsNullOrEmpty(value) && Library.Utility.Utility.ParseBool(value, true) != Library.Utility.Utility.ParseBool(value, false))
                    return string.Format(Strings.Controller.UnsupportedBooleanValue, optionname, value);
            }
            else if (arg.Type == Duplicati.Library.Interface.CommandLineArgument.ArgumentType.Integer)
            {
                long l;
                if (!long.TryParse(value, out l))
                    return string.Format(Strings.Controller.UnsupportedIntegerValue, optionname, value);
            }
            else if (arg.Type == Duplicati.Library.Interface.CommandLineArgument.ArgumentType.Path)
            {
                foreach (string p in value.Split(System.IO.Path.DirectorySeparatorChar))
                    if (p.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0)
                        return string.Format(Strings.Controller.UnsupportedPathValue, optionname, p);
            }
            else if (arg.Type == Duplicati.Library.Interface.CommandLineArgument.ArgumentType.Size)
            {
                try
                {
                    Library.Utility.Sizeparser.ParseSize(value);
                }
                catch
                {
                    return string.Format(Strings.Controller.UnsupportedSizeValue, optionname, value);
                }
            }
            else if (arg.Type == Duplicati.Library.Interface.CommandLineArgument.ArgumentType.Timespan)
            {
                try
                {
                    Library.Utility.Timeparser.ParseTimeSpan(value);
                }
                catch
                {
                    return string.Format(Strings.Controller.UnsupportedTimeValue, optionname, value);
                }
            }

            return null;
        }
        
        public void Pause()
        {
            var ct = m_currentTask;
            if (ct != null)
                ct.Pause();
        }

        public void Resume()
        {
            var ct = m_currentTask;
            if (ct != null)
                ct.Resume();
        }

        public void Stop()
        {
            var ct = m_currentTask;
            if (ct != null)
                ct.Stop();
        }

        public void Abort()
        {
            var ct = m_currentTask;
            if (ct != null)
                ct.Abort();
            
            var t = m_currentTaskThread;
            if (t != null)
                t.Abort();
        }

        #region IDisposable Members

        public void Dispose()
        {
        }

        #endregion
    }
}
