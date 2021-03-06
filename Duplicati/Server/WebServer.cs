using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HttpServer.HttpModules;
using System.IO;
using Duplicati.Server.Serialization;

namespace Duplicati.Server
{
    public class WebServer
    {
        /// <summary>
        /// Option for changing the webroot folder
        /// </summary>
        public const string OPTION_WEBROOT = "webservice-webroot";
        /// <summary>
        /// Option for changing the webservice listen port
        /// </summary>
        public const string OPTION_PORT = "webservice-port";

        /// <summary>
        /// The single webserver instance
        /// </summary>
        private HttpServer.HttpServer m_server;
        
        /// <summary>
        /// The webserver listening port
        /// </summary>
        public readonly int Port;
        
        /// <summary>
        /// A string that is sent out instead of password values
        /// </summary>
        public const string PASSWORD_PLACEHOLDER = "**********";

        /// <summary>
        /// Sets up the webserver and starts it
        /// </summary>
        /// <param name="options">A set of options</param>
        public WebServer(IDictionary<string, string> options)
        {
            int port;
            string portstring;
            IEnumerable<int> ports = null;
            options.TryGetValue(OPTION_PORT, out portstring);
            if (!string.IsNullOrEmpty(portstring))
                ports = 
                    from n in portstring.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                where int.TryParse(n, out port)
                                select int.Parse(n);

            if (ports == null || !ports.Any())
                ports = new int[] { 8080 };                                

            // If we are in hosted mode with no specified port, 
            // then try different ports
            foreach(var p in ports)
                try
                {
                    // Due to the way the server is initialized, 
                    // we cannot try to start it again on another port, 
                    // so we create a new server for each attempt
                
                    var server = CreateServer(options);
                    server.Start(System.Net.IPAddress.Any, p);
                    m_server = server;
                    m_server.ServerName = "Duplicati v" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
                    this.Port = p;
                    return;
                }
                catch (System.Net.Sockets.SocketException)
                {
                }
                
            throw new Exception("Unable to open a socket for listening, tried ports: " + string.Join(",", from n in ports select n.ToString()));
        }
        
        private static HttpServer.HttpServer CreateServer(IDictionary<string, string> options)
        {
            HttpServer.HttpServer server = new HttpServer.HttpServer();

            server.Add(new DynamicHandler());

            string webroot = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
#if DEBUG
            //For debug we go "../../../.." to get out of "GUI/Duplicati.GUI.TrayIcon/bin/debug"
            string tmpwebroot = System.IO.Path.GetFullPath(System.IO.Path.Combine(webroot, "..", "..", "..", ".."));
            tmpwebroot = System.IO.Path.Combine(tmpwebroot, "Server");
            if (System.IO.Directory.Exists(System.IO.Path.Combine(tmpwebroot, "webroot")))
                webroot = tmpwebroot;
            else
            {
                //If we are running the server standalone, we only need to exit "bin/Debug"
                tmpwebroot = System.IO.Path.GetFullPath(System.IO.Path.Combine(webroot, "..", ".."));
                if (System.IO.Directory.Exists(System.IO.Path.Combine(tmpwebroot, "webroot")))
                    webroot = tmpwebroot;
            }

            if (Library.Utility.Utility.IsClientOSX)
            {
                string osxTmpWebRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(webroot, "..", "..", "..", "..", "..", "..", ".."));
                osxTmpWebRoot = System.IO.Path.Combine(osxTmpWebRoot, "Server");
                if (System.IO.Directory.Exists(System.IO.Path.Combine(osxTmpWebRoot, "webroot")))
                    webroot = osxTmpWebRoot;
            }
#endif

            webroot = System.IO.Path.Combine(webroot, "webroot");

            if (options.ContainsKey(OPTION_WEBROOT))
            {
                string userroot = options[OPTION_WEBROOT];
#if DEBUG
                //In debug mode we do not care where the path points
#else
                //In release mode we check that the user supplied path is located
                // in the same folders as the running application, to avoid users
                // that inadvertently expose top level folders
                if (!string.IsNullOrWhiteSpace(userroot)
                    &&
                    (
                        userroot.StartsWith(Library.Utility.Utility.AppendDirSeparator(System.Reflection.Assembly.GetExecutingAssembly().Location), Library.Utility.Utility.ClientFilenameStringComparision)
                        ||
                        userroot.StartsWith(Library.Utility.Utility.AppendDirSeparator(Program.StartupPath), Library.Utility.Utility.ClientFilenameStringComparision)
                    )
                )
#endif
                {
                    webroot = userroot;
                }
            }

            FileModule fh = new FileModule("/", webroot);
            fh.AddDefaultMimeTypes();
            fh.MimeTypes.Add("htc", "text/x-component");
            fh.MimeTypes.Add("json", "application/json");
            fh.MimeTypes.Add("map", "application/json");
            server.Add(fh);
            server.Add(new IndexHtmlHandler(System.IO.Path.Combine(webroot, "index.html")));
#if DEBUG
            //For debugging, it is nice to know when we get a 404
            server.Add(new DebugReportHandler());
#endif
            
            return server;
        }

        private class BodyWriter : System.IO.StreamWriter, IDisposable
        {
            private HttpServer.IHttpResponse m_resp;

            // We override the format provider so all JSON output uses US format
            public override IFormatProvider FormatProvider
            {
                get { return System.Globalization.CultureInfo.InvariantCulture; }
            }

            public BodyWriter(HttpServer.IHttpResponse resp)
                : base(resp.Body,  resp.Encoding)
            {
                m_resp = resp;
            }

            protected override void Dispose (bool disposing)
            {
                if (!m_resp.HeadersSent)
                {
                    base.Flush();
                    m_resp.ContentLength = base.BaseStream.Length;
                    m_resp.Send();
                }
                base.Dispose(disposing);
            }
        }

        private class IndexHtmlHandler : HttpModule
        {
            private string m_defaultdoc;

            public IndexHtmlHandler(string defaultdoc) { m_defaultdoc = defaultdoc; }

            public override bool Process(HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session)
            {
                if ((request.Uri.AbsolutePath == "/" || request.Uri.AbsolutePath == "/index.html" || request.Uri.AbsolutePath == "/index.htm") && System.IO.File.Exists(m_defaultdoc))
                {
                    response.Status = System.Net.HttpStatusCode.OK;
                    response.Reason = "OK";
                    response.ContentType = "text/html";

                    using (var fs = System.IO.File.OpenRead(m_defaultdoc))
                    {
                        response.ContentLength = fs.Length;
                        response.Body = fs;
                        response.Send();
                    }

                    return true;
                }

                return false;
            }
        }

        private class DebugReportHandler : HttpModule
        {
            public override bool Process(HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session)
            {
                System.Diagnostics.Trace.WriteLine(string.Format("Rejecting request for {0}", request.Uri));
                return false;
            }
        }

        private class DynamicHandler : HttpModule
        {
            private delegate void ProcessSub(HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session, BodyWriter writer);
            private readonly Dictionary<string, ProcessSub> SUPPORTED_METHODS;

            public DynamicHandler()
            {
                SUPPORTED_METHODS = new Dictionary<string, ProcessSub>(System.StringComparer.InvariantCultureIgnoreCase);
             
                //Make a list of all supported actions
                SUPPORTED_METHODS.Add("supported-actions", ListSupportedActions);
                SUPPORTED_METHODS.Add("system-info", ListSystemInfo);
                SUPPORTED_METHODS.Add("list-backups", ListBackups);
                SUPPORTED_METHODS.Add("get-current-state", GetCurrentState);
                SUPPORTED_METHODS.Add("get-progress-state", GetProgressState);
                SUPPORTED_METHODS.Add("list-application-settings", ListApplicationSettings);
                SUPPORTED_METHODS.Add("list-options", ListCoreOptions);
                SUPPORTED_METHODS.Add("send-command", SendCommand);
                SUPPORTED_METHODS.Add("get-backup-defaults", GetBackupDefaults);
                SUPPORTED_METHODS.Add("get-folder-contents", GetFolderContents);
                SUPPORTED_METHODS.Add("get-backup", GetBackup);
                SUPPORTED_METHODS.Add("add-backup", AddBackup);
                SUPPORTED_METHODS.Add("update-backup", UpdateBackup);
                SUPPORTED_METHODS.Add("delete-backup", DeleteBackup);
                SUPPORTED_METHODS.Add("validate-path", ValidatePath);
                SUPPORTED_METHODS.Add("list-tags", ListTags);
                SUPPORTED_METHODS.Add("test-backend", TestBackend);
                SUPPORTED_METHODS.Add("list-remote-folder", ListRemoteFolder);
                SUPPORTED_METHODS.Add("list-backup-sets", ListBackupSets);
                SUPPORTED_METHODS.Add("search-backup-files", SearchBackupFiles);
                SUPPORTED_METHODS.Add("restore-files", RestoreFiles);
                SUPPORTED_METHODS.Add("read-log", ReadLogData);
                SUPPORTED_METHODS.Add("get-license-data", GetLicenseData);
            }

            public override bool Process (HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session)
            {
                //We use the fake entry point /control.cgi to listen for requests
                //This ensures that the rest of the webserver can just serve plain files
                if (!request.Uri.AbsolutePath.Equals("/control.cgi", StringComparison.InvariantCultureIgnoreCase))
                    return false;

                HttpServer.HttpInput input = request.Method.ToUpper() == "POST" ? request.Form : request.QueryString;

                string action = input["action"].Value ?? "";
             
                //Lookup the actual handler method
                ProcessSub method;
                SUPPORTED_METHODS.TryGetValue(action, out method);

                if (method == null) {
                    response.Status = System.Net.HttpStatusCode.NotImplemented;
                    response.Reason = "Unsupported action: " + (action == null ? "<null>" : "");
                    response.Send();
                } else {
                    //Default setup
                    response.Status = System.Net.HttpStatusCode.OK;
                    response.Reason = "OK";
#if DEBUG
                    response.ContentType = "text/plain";
#else
                    response.ContentType = "text/json";
#endif
                    using (BodyWriter bw = new BodyWriter(response))
                    {
                        try
                        {
                            method(request, response, session, bw);
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                if (!response.HeadersSent)
                                {
                                    response.Status = System.Net.HttpStatusCode.InternalServerError;
                                    response.Reason = "Error";
                                    response.ContentType = "text/plain";

                                    OutputObject(bw, new
                                    {
                                        Message = ex.Message,
                                        Type = ex.GetType().Name,
#if DEBUG
                                        Stacktrace = ex.ToString()
#endif
                                    });
                                    bw.Flush();
                                }
                            }
                            catch (Exception flex)
                            {
                                Program.DataConnection.LogError("", "Handling outer ex", ex);
                                Program.DataConnection.LogError("", "Gaver inner ex", flex);
                            }
                        }
                    }
                }

                return true;
            }

            private void ReportError(HttpServer.IHttpResponse response, BodyWriter bw, string message)
            {
                response.Status = System.Net.HttpStatusCode.InternalServerError;
                response.Reason = message;

                OutputObject(bw, new { Error = message });
            }
            
            private void OutputObject (BodyWriter b, object o)
            {
                Serializer.SerializeJson(b, o);
            }
            
            private List<Dictionary<string, object>> DumpTable(System.Data.IDbCommand cmd, string tablename, string pagingfield, string offset_str, string pagesize_str)
            {
                var result = new List<Dictionary<string, object>>();
                
                long pagesize;
                if (!long.TryParse(pagesize_str, out pagesize))
                    pagesize = 100;
                
                pagesize = Math.Max(10, Math.Min(500, pagesize));
                
                cmd.CommandText = "SELECT * FROM \"" + tablename + "\"";
                long offset = 0;
                if (!string.IsNullOrWhiteSpace(offset_str) && long.TryParse(offset_str, out offset) && !string.IsNullOrEmpty(pagingfield))
                {
                    var p = cmd.CreateParameter();
                    p.Value = offset;
                    cmd.Parameters.Add(p);
                    
                    cmd.CommandText += " WHERE \"" + pagingfield + "\" < ?";
                }
                
                if (!string.IsNullOrEmpty(pagingfield))
                    cmd.CommandText += " ORDER BY \"" + pagingfield + "\" DESC";
                cmd.CommandText += " LIMIT " + pagesize.ToString();
                
                using(var rd = cmd.ExecuteReader())
                {
                    var names = new List<string>();
                    for(var i = 0; i < rd.FieldCount; i++)
                        names.Add(rd.GetName(i));
                    
                    while (rd.Read())
                    {
                        var dict = new Dictionary<string, object>();
                        for(int i = 0; i < names.Count; i++)
                            dict[names[i]] = rd.GetValue(i);
                        
                        result.Add(dict);                                    
                    }
                }
                
                return result;
            }
            
            private void GetLicenseData(HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session, BodyWriter bw)
            {
                OutputObject(bw, License.LicenseReader.ReadLicenses(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location), "licenses")));
            }
            
            private void ReadLogData(HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session, BodyWriter bw)
            {
                HttpServer.HttpInput input = request.Method.ToUpper() == "POST" ? request.Form : request.QueryString;
                var backupid = input["id"].Value;
                
                if (string.IsNullOrWhiteSpace(backupid))
                {
                    List<Dictionary<string, object>> res = null;
                    Program.DataConnection.ExecuteWithCommand(x =>
                    {
                        res = DumpTable(x, "ErrorLog", "Timestamp", input["offset"].Value, input["pagesize"].Value);
                    });
                    
                    OutputObject(bw, res);
                }
                else
                {
                    var backup = Program.DataConnection.GetBackup(backupid);
                    if (backup == null)
                    {
                        ReportError(response, bw, "Invalid or missing backup id");
                        return;
                    }
                    
                    using(var con = (System.Data.IDbConnection)Activator.CreateInstance(Duplicati.Library.SQLiteHelper.SQLiteLoader.SQLiteConnectionType))
                    {
                        con.ConnectionString = "Data Source=" + backup.DBPath;
                        con.Open();
                        
                        using(var cmd = con.CreateCommand())
                        {
                            if (Duplicati.Library.Utility.Utility.ParseBool(input["remotelog"].Value, false))
                            {
                                var dt = DumpTable(cmd, "RemoteOperation", "ID", input["offset"].Value, input["pagesize"].Value);

                                // Unwrap raw data to a string
                                foreach(var n in dt)
                                    try { n["Data"] = System.Text.Encoding.UTF8.GetString((byte[])n["Data"]); }
                                    catch { }

                                OutputObject(bw, dt);
                            }
                            else
                            {
                                var dt = DumpTable(cmd, "LogData", "ID", input["offset"].Value, input["pagesize"].Value);
                                OutputObject(bw, dt);
                            }
                        }
                    }
                }
            }
            
            private void RestoreFiles(HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session, BodyWriter bw)
            {
                HttpServer.HttpInput input = request.Method.ToUpper() == "POST" ? request.Form : request.QueryString;
                var bk = Program.DataConnection.GetBackup(input["id"].Value);
                if (bk == null)
                {
                    ReportError(response, bw, "Invalid or missing backup id");
                    return;
                }

                var filters = input["paths"].Value.Split(new string[] { System.IO.Path.PathSeparator.ToString() }, StringSplitOptions.RemoveEmptyEntries);
                var time = Duplicati.Library.Utility.Timeparser.ParseTimeInterval(input["time"].Value, DateTime.Now);
                var restoreTarget = input["restore-path"].Value;
                var overwrite = Duplicati.Library.Utility.Utility.ParseBool(input["overwrite"].Value, false);
                var task = Runner.CreateRestoreTask(bk, filters, time, restoreTarget, overwrite);
                Program.WorkThread.AddTask(task);
                
                OutputObject(bw, new { TaskID = task.TaskID });
                
            }
            
            private void ListBackupSets(HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session, BodyWriter bw)
            {
                HttpServer.HttpInput input = request.Method.ToUpper() == "POST" ? request.Form : request.QueryString;
                var bk = Program.DataConnection.GetBackup(input["id"].Value);
                if (bk == null)
                {
                    ReportError(response, bw, "Invalid or missing backup id");
                    return;
                }
                
                
                var r = Runner.Run(Runner.CreateTask(DuplicatiOperation.List, bk), false) as Duplicati.Library.Interface.IListResults;
                
                OutputObject(bw, r.Filesets);
            }
            
            private void SearchBackupFiles(HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session, BodyWriter bw)
            {
                HttpServer.HttpInput input = request.Method.ToUpper() == "POST" ? request.Form : request.QueryString;
                                
                var filter = (input["filter"].Value ?? "").Split(new string[] { System.IO.Path.PathSeparator.ToString() }, StringSplitOptions.RemoveEmptyEntries);
                var timestring = input["time"].Value;
                var allversion = Duplicati.Library.Utility.Utility.ParseBool(input["all-versions"].Value, false);
                
                if (string.IsNullOrWhiteSpace(timestring) && !allversion)
                {
                    ReportError(response, bw, "Invalid or missing time");
                    return;
                }
                var bk = Program.DataConnection.GetBackup(input["id"].Value);
                if (bk == null)
                {
                    ReportError(response, bw, "Invalid or missing backup id");
                    return;
                }
                
                var prefixonly = Duplicati.Library.Utility.Utility.ParseBool(input["prefix-only"].Value, false);
                var foldercontents = Duplicati.Library.Utility.Utility.ParseBool(input["folder-contents"].Value, false);
                var time = new DateTime();
                if (!allversion)
                    time = Duplicati.Library.Utility.Timeparser.ParseTimeInterval(timestring, DateTime.Now);
                                
                var r = Runner.Run(Runner.CreateListTask(bk, filter, prefixonly, allversion, foldercontents, time), false) as Duplicati.Library.Interface.IListResults;
                
                var result = new Dictionary<string, object>();
                
                foreach(HttpServer.HttpInputItem n in input)
                    result[n.Name] = n.Value;
                
                result["Filesets"] = r.Filesets;
                result["Files"] = r.Files;
                
                OutputObject(bw, result);
            }
            
            
            private void ListRemoteFolder(HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session, BodyWriter bw)
            {
                HttpServer.HttpInput input = request.Method.ToUpper() == "POST" ? request.Form : request.QueryString;
                if (input["url"] == null || input["url"].Value == null)
                {
                    ReportError(response, bw, "The url parameter was not set");
                    return;
                }
                
                try
                {
                    using(var b = Duplicati.Library.DynamicLoader.BackendLoader.GetBackend(input["url"].Value, new Dictionary<string, string>()))
                        OutputObject(bw, new { Status = "OK", Folders = b.List() });
                    
                }
                catch (Exception ex)
                {
                    ReportError(response, bw, ex.Message);
                }
            }

            private void TestBackend(HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session, BodyWriter bw)
            {
                HttpServer.HttpInput input = request.Method.ToUpper() == "POST" ? request.Form : request.QueryString;
                if (input["url"] == null || input["url"].Value == null)
                {
                    ReportError(response, bw, "The url parameter was not set");
                    return;
                }
                
                try
                {
                    using(var b = Duplicati.Library.DynamicLoader.BackendLoader.GetBackend(input["url"].Value, new Dictionary<string, string>()))
                        b.Test();
                    
                    OutputObject(bw, new { Status = "OK" });
                }
                catch (Exception ex)
                {
                    ReportError(response, bw, ex.Message);
                }
            }

            private void ListSystemInfo(HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session, BodyWriter bw)
            {
                OutputObject(bw, new
                {
                    APIVersion = 1,
                    PasswordPlaceholder = PASSWORD_PLACEHOLDER,
                    ServerVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(),
                    ServerVersionName = License.VersionNumbers.Version,
                    ServerTime = DateTime.Now,
                    OSType = Library.Utility.Utility.IsClientLinux ? (Library.Utility.Utility.IsClientOSX ? "OSX" : "Linux") : "Windows",
                    DirectorySeparator = System.IO.Path.DirectorySeparatorChar,
                    PathSeparator = System.IO.Path.PathSeparator,
                    CaseSensitiveFilesystem = Duplicati.Library.Utility.Utility.IsFSCaseSensitive,
                    MonoVersion = Duplicati.Library.Utility.Utility.IsMono ? Duplicati.Library.Utility.Utility.MonoVersion.ToString() : null,
                    MachineName = System.Environment.MachineName,
                    NewLine = System.Environment.NewLine,
                    CLRVersion = System.Environment.Version.ToString(),
                    CLROSInfo = new
                    {
                        Platform = System.Environment.OSVersion.Platform.ToString(),
                        ServicePack = System.Environment.OSVersion.ServicePack,
                        Version = System.Environment.OSVersion.Version.ToString(),
                        VersionString = System.Environment.OSVersion.VersionString
                    },
                    Options = Serializable.ServerSettings.Options,
                    CompressionModules =  Serializable.ServerSettings.CompressionModules,
                    EncryptionModules = Serializable.ServerSettings.EncryptionModules,
                    BackendModules = Serializable.ServerSettings.BackendModules,
                    GenericModules = Serializable.ServerSettings.GenericModules,
                    WebModules = Serializable.ServerSettings.WebModules
                });
            }

            private void ListSupportedActions(HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session, BodyWriter bw)
            {
                OutputObject(bw, new { Version = 1, Methods = SUPPORTED_METHODS.Keys });
            }

            private void ListBackups (HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session, BodyWriter bw)
            {
                var schedules = Program.DataConnection.Schedules;
                var backups = Program.DataConnection.Backups;
                
                var all = from n in backups
                select new AddOrUpdateBackupData() {
                    Backup = (Database.Backup)n,
                    Schedule = 
                        (from x in schedules
                            where x.Tags != null && x.Tags.Contains("ID=" + n.ID)
                            select (Database.Schedule)x).FirstOrDefault()
                };
                
                OutputObject(bw, all.ToArray());
            }

            private void ListTags(HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session, BodyWriter bw)
            {
                var r = 
                    from n in 
                    Serializable.ServerSettings.CompressionModules
                        .Union(Serializable.ServerSettings.EncryptionModules)
                        .Union(Serializable.ServerSettings.BackendModules)
                        .Union(Serializable.ServerSettings.GenericModules)
                        select n.Key.ToLower();
                
                // Append all known tags
                r = r.Union(from n in Program.DataConnection.Backups select n.Tags into p from x in p select x.ToLower());
                OutputObject(bw, r);
            }

            private void ValidatePath(HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session, BodyWriter bw)
            {
                HttpServer.HttpInput input = request.Method.ToUpper() == "POST" ? request.Form : request.QueryString;
                if (input["path"] == null || input["path"].Value == null)
                {
                    ReportError(response, bw, "The path parameter was not set");
                    return;
                }
                
                try
                {
                    string path = SpecialFolders.ExpandEnvironmentVariables(input["path"].Value);                
                    if (System.IO.Path.IsPathRooted(path) && System.IO.Directory.Exists(path))
                    {
                        OutputObject(bw, new { Status = "OK" });
                        return;
                    }
                }
                catch
                {
                }
                
                ReportError(response, bw, "File or folder not found");
                return;
            }

            private static IEnumerable<Serializable.TreeNode> ListFolderAsNodes(string entrypath, bool skipFiles)
            {
                //Helper function for finding out if a folder has sub elements
                Func<string, bool> hasSubElements = (p) => skipFiles ? Directory.EnumerateDirectories(p).Any() : Directory.EnumerateFileSystemEntries(p).Any();

                //Helper function for dealing with exceptions when accessing off-limits folders
                Func<string, bool> isEmptyFolder = (p) =>
                {
                    try { return !hasSubElements(p); }
                    catch { }
                    return true;
                };

                //Helper function for dealing with exceptions when accessing off-limits folders
                Func<string, bool> canAccess = (p) =>
                {
                    try { hasSubElements(p); return true; }
                    catch { }
                    return false;
                }; 
                
                var systemIO = Library.Utility.Utility.IsClientLinux
                    ? (Duplicati.Library.Snapshots.ISystemIO)new Duplicati.Library.Snapshots.SystemIOLinux() 
                    : (Duplicati.Library.Snapshots.ISystemIO)new Duplicati.Library.Snapshots.SystemIOWindows();

                foreach (var s in System.IO.Directory.EnumerateFileSystemEntries(entrypath))
                {
                    Serializable.TreeNode tn = null;
                    try
                    {
                        var attr = systemIO.GetFileAttributes(s);
                        //var isSymlink = (attr & FileAttributes.ReparsePoint) != 0;
                        var isFolder = (attr & FileAttributes.Directory) != 0;
                        var isFile = !isFolder;
                        //var isHidden = (attr & FileAttributes.Hidden) != 0;

                        var accesible = isFile || canAccess(s);
                        var isLeaf = isFile || !accesible || isEmptyFolder(s);

                        var rawid = isFolder ? Library.Utility.Utility.AppendDirSeparator(s) : s;
                        if (!skipFiles || isFolder)
                            tn = new Serializable.TreeNode()
                            {
                                id = rawid,
                                text = systemIO.PathGetFileName(s),
                                iconCls = isFolder ? (accesible ? "x-tree-icon-parent" : "x-tree-icon-locked") : "x-tree-icon-leaf",
                                leaf = isLeaf
                            };
                    }
                    catch
                    {
                    }

                    if (tn != null)
                        yield return tn;
                }
            }

            private void GetFolderContents(HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session, BodyWriter bw)
            {
                HttpServer.HttpInput input = request.Method.ToUpper() == "POST" ? request.Form : request.QueryString;
                if (input["path"] == null || input["path"].Value == null)
                {
                    ReportError(response, bw, "The path parameter was not set");
                    return;
                }

                bool skipFiles = Library.Utility.Utility.ParseBool(input["onlyfolders"].Value, false);

                var path = input["path"].Value;
                string specialpath = null;
                string specialtoken = null;
                
                if (path.StartsWith("%"))
                {
                    var ix = path.IndexOf("%", 1);
                    if (ix > 0)
                    {
                        var tk = path.Substring(0, ix + 1);
                        var node = SpecialFolders.Nodes.Where(x => x.id.Equals(tk, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
                        if (node != null)
                        {
                            specialpath = node.resolvedpath;
                            specialtoken = node.id;
                        }
                    }
                }
                
                path = SpecialFolders.ExpandEnvironmentVariables(path);
                
                if (Duplicati.Library.Utility.Utility.IsClientLinux && !path.StartsWith("/"))
                {
                    ReportError(response, bw, "The path parameter must start with a forward-slash");
                    return;
                }

                try
                {
                    if (path != "" && path != "/")
                        path = Duplicati.Library.Utility.Utility.AppendDirSeparator(path);

                    IEnumerable<Serializable.TreeNode> res;

                    if (!Library.Utility.Utility.IsClientLinux && (path.Equals("/") || path.Equals("")))
                    {
                        res = 
                            from di in System.IO.DriveInfo.GetDrives()
                            where di.DriveType == DriveType.Fixed || di.DriveType == DriveType.Network || di.DriveType == DriveType.Removable
                            select new Serializable.TreeNode()
                            {
                                id = di.RootDirectory.FullName,
                                text = di.RootDirectory.FullName.Replace('\\', ' ') + "(" + di.DriveType + ")",
                                iconCls = "x-tree-icon-drive"
                            };
                    }
                    else
                    {
                        res = ListFolderAsNodes(path, skipFiles);                        
                    }

                    if ((path.Equals("/") || path.Equals("")) && specialtoken == null) 
                    {
                        // Prepend special folders
                        res = SpecialFolders.Nodes.Union(res);
                    }
                    
                    if (specialtoken != null)
                    {
                        res = from n in res
                            select new Serializable.TreeNode() {
                            id = specialtoken + n.id.Substring(specialpath.Length),
                            text = n.text,
                            iconCls = n.iconCls,
                            leaf = n.leaf,
                            resolvedpath = n.id
                            };
                    }
                    
                    OutputObject(bw, res);
                }
                catch (Exception ex)
                {
                    ReportError(response, bw, "Failed to process the path: " + ex.Message);
                }
            }       

            private bool LongPollCheck(HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, BodyWriter bw, EventPollNotify poller, ref long id, out bool isError)
            {
                HttpServer.HttpInput input = request.Method.ToUpper() == "POST" ? request.Form : request.QueryString;
                if (Library.Utility.Utility.ParseBool(input["longpoll"].Value, false))
                {
                    long lastEventId;
                    if (!long.TryParse(input["lasteventid"].Value, out lastEventId))
                    {
                        ReportError(response, bw, "When activating long poll, the request must include the last event id");
                        isError = true;
                        return false;
                    }

                    TimeSpan ts;
                    try { ts = Library.Utility.Timeparser.ParseTimeSpan(input["duration"].Value); }
                    catch (Exception ex)
                    {
                        ReportError(response, bw, "Invalid duration: " + ex.Message);
                        isError = true;
                        return false;
                    }

                    if (ts <= TimeSpan.FromSeconds(10) || ts.TotalMilliseconds > int.MaxValue)
                    {
                        ReportError(response, bw, "Invalid duration, must be at least 10 seconds, and less than " + int.MaxValue + " milliseconds");
                        isError = true;
                        return false;
                    }

                    isError = false;
                    id = poller.Wait(lastEventId, (int)ts.TotalMilliseconds);
                    return true;
                }

                isError = false;
                return false;
            }

            private void GetProgressState(HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session, BodyWriter bw)
            {
                if (Program.GenerateProgressState == null)
                {
                    ReportError(response, bw, "No active backup");
                }
                else
                {
                    var ev = Program.GenerateProgressState();
                    OutputObject(bw, ev);
                }
            }

            private void GetCurrentState (HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session, BodyWriter bw)
            {
                bool isError;
                long id = 0;
                if (LongPollCheck(request, response, bw, Program.StatusEventNotifyer, ref id, out isError))
                {
                    //Make sure we do not report a higher number than the eventnotifyer says
                    var st = new Serializable.ServerStatus();
                    st.LastEventID = id;
                    OutputObject(bw, st);
                }
                else if (!isError)
                {
                    OutputObject(bw, new Serializable.ServerStatus());
                }
            }

            private void ListCoreOptions(HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session, BodyWriter bw)
            {
                OutputObject(bw, new Duplicati.Library.Main.Options(new Dictionary<string, string>()).SupportedCommands);
            }

            private void ListApplicationSettings(HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session, BodyWriter bw)
            {
                OutputObject(bw, Program.DataConnection.ApplicationSettings);
            }
            
            private static void MergeJsonObjects(Newtonsoft.Json.Linq.JObject self, Newtonsoft.Json.Linq.JObject other)
            {
                foreach(var p in other.Properties())
                {
                    var sp = self.Property(p.Name);
                    if (sp == null)
                        self.Add(p);
                    else
                    {
                        switch (p.Type)
                        {
                            // Primitives override
                            case Newtonsoft.Json.Linq.JTokenType.Boolean:
                            case Newtonsoft.Json.Linq.JTokenType.Bytes:
                            case Newtonsoft.Json.Linq.JTokenType.Comment:
                            case Newtonsoft.Json.Linq.JTokenType.Constructor:
                            case Newtonsoft.Json.Linq.JTokenType.Date:
                            case Newtonsoft.Json.Linq.JTokenType.Float:
                            case Newtonsoft.Json.Linq.JTokenType.Guid:
                            case Newtonsoft.Json.Linq.JTokenType.Integer:
                            case Newtonsoft.Json.Linq.JTokenType.String:
                            case Newtonsoft.Json.Linq.JTokenType.TimeSpan:
                            case Newtonsoft.Json.Linq.JTokenType.Uri:
                            case Newtonsoft.Json.Linq.JTokenType.None:
                            case Newtonsoft.Json.Linq.JTokenType.Null:
                            case Newtonsoft.Json.Linq.JTokenType.Undefined:
                                self.Replace(p);
                                break;

                            // Arrays merge
                            case Newtonsoft.Json.Linq.JTokenType.Array:
                                if (sp.Type == Newtonsoft.Json.Linq.JTokenType.Array)
                                    sp.Value = new Newtonsoft.Json.Linq.JArray(((Newtonsoft.Json.Linq.JArray)sp.Value).Union((Newtonsoft.Json.Linq.JArray)p.Value));
                                else
                                {
                                    var a = new Newtonsoft.Json.Linq.JArray(sp.Value);
                                    sp.Value = new Newtonsoft.Json.Linq.JArray(a.Union((Newtonsoft.Json.Linq.JArray)p.Value));
                                }
                                
                                break;
                                
                            // Objects merge
                            case Newtonsoft.Json.Linq.JTokenType.Object:
                                if (sp.Type == Newtonsoft.Json.Linq.JTokenType.Object)
                                    MergeJsonObjects((Newtonsoft.Json.Linq.JObject)sp.Value, (Newtonsoft.Json.Linq.JObject)p.Value);
                                else
                                    sp.Value = p.Value;
                                break;
                                
                            // Ignore other stuff                                
                            default:
                                break;
                        }
                    }
                }
            }

            private void GetBackupDefaults(HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session, BodyWriter bw)
            {   
                // Start with a scratch object
                var o = new Newtonsoft.Json.Linq.JObject();
                
                // Add application wide settings
                o.Add("ApplicationOptions", new Newtonsoft.Json.Linq.JArray(Program.DataConnection.Settings));
                
                try
                {
                    // Add built-in defaults
                    Newtonsoft.Json.Linq.JObject n;
                    using(var s = new System.IO.StreamReader(System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(System.Reflection.Assembly.GetExecutingAssembly().GetName().Name + ".newbackup.json")))
                        n = (Newtonsoft.Json.Linq.JObject)Newtonsoft.Json.JsonConvert.DeserializeObject(s.ReadToEnd());
                    
                    MergeJsonObjects(o, n);
                }
                catch
                {
                }

                try
                {
                    // Add install defaults/overrides, if present
                    var path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "newbackup.json");
                    if (System.IO.File.Exists(path))
                    {
                        Newtonsoft.Json.Linq.JObject n;
                        n = (Newtonsoft.Json.Linq.JObject)Newtonsoft.Json.JsonConvert.DeserializeObject(System.IO.File.ReadAllText(path));
                        
                        MergeJsonObjects(o, n);
                    }
                }
                catch
                {
                }

                OutputObject(bw, new
                {
                    success = true,
                    data = o
                });
            }

            private void GetBackup(HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session, BodyWriter bw)
            {
                HttpServer.HttpInput input = request.Method.ToUpper() == "POST" ? request.Form : request.QueryString;
                var bk = Program.DataConnection.GetBackup(input["id"].Value);
                if (bk == null)
                    ReportError(response, bw, "Invalid or missing backup id");
                else
                {
                    var systemIO = Library.Utility.Utility.IsClientLinux
                        ? (Duplicati.Library.Snapshots.ISystemIO)new Duplicati.Library.Snapshots.SystemIOLinux()
                        : (Duplicati.Library.Snapshots.ISystemIO)new Duplicati.Library.Snapshots.SystemIOWindows();

                    var scheduleId = Program.DataConnection.GetScheduleIDsFromTags(new string[] { "ID=" + bk.ID });
                    var schedule = scheduleId.Any() ? Program.DataConnection.GetSchedule(scheduleId.First()) : null;
                    var sourcenames = bk.Sources.Distinct().Select(x => {
                        var sp = SpecialFolders.TranslateToDisplayString(x);
                        if (sp != null)
                            return new KeyValuePair<string, string>(x, sp);

                        x = SpecialFolders.ExpandEnvironmentVariables(x);
                        try {
                            var nx = x;
                            if (nx.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString()))
                                nx = nx.Substring(0, nx.Length - 1);
                            var n = systemIO.PathGetFileName(nx);
                            if (!string.IsNullOrWhiteSpace(n))
                                return new KeyValuePair<string, string>(x, n);
                        } catch {
                        }
                        
                        if (x.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString()) && x.Length > 1)
                            return new KeyValuePair<string, string>(x, x.Substring(0, x.Length - 1).Substring(x.Substring(0, x.Length - 1).LastIndexOf("/") + 1));
                        else
                            return new KeyValuePair<string, string>(x, x);
                        
                    }).ToDictionary(x => x.Key, x => x.Value);
                    
                    //TODO: Filter out the password in both settings and the target url
                    
                    OutputObject(bw, new
                    {
                        success = true,
                        data = new {
                            Schedule = schedule,
                            Backup = bk,
                            DisplayNames = sourcenames
                        }
                    });
                }
            }

            private void UpdateBackup(HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session, BodyWriter bw)
            {
                string str = request.Form["data"].Value;
                if (string.IsNullOrWhiteSpace(str))
                {
                    ReportError(response, bw, "Missing backup object");
                    return;
                }

                AddOrUpdateBackupData data = null;
                try
                {
                    data = Serializer.Deserialize<AddOrUpdateBackupData>(new StringReader(str));
                    if (data.Backup == null)
                    {
                        ReportError(response, bw, "Data object had no backup entry");
                        return;
                    }
                    
                    if (data.Backup.ID == null)
                    {
                        ReportError(response, bw, "Invalid or missing backup id");
                        return;
                    }                    
                    
                    if (data.Backup.IsTemporary)
                    {
                        var backup = Program.DataConnection.GetBackup(data.Backup.ID);
                        if (backup.IsTemporary)
                            throw new InvalidDataException("External is temporary but internal is not?");
                        
                        Program.DataConnection.UpdateTemporaryBackup(backup);
                        OutputObject(bw, new { status = "OK" });
                    }
                    else
                    {                    
                        lock(Program.DataConnection.m_lock)
                        {
                            var backup = Program.DataConnection.GetBackup(data.Backup.ID);
                            if (backup == null)
                            {
                                ReportError(response, bw, "Invalid or missing backup id");
                                return;
                            }
        
                            if (Program.DataConnection.Backups.Where(x => x.Name.Equals(data.Backup.Name, StringComparison.InvariantCultureIgnoreCase) && x.ID != data.Backup.ID).Any())
                            {
                                ReportError(response, bw, "There already exists a backup with the name: " + data.Backup.Name);
                                return;
                            }
                            
                            //TODO: Merge in real passwords where the placeholder is found
                            Program.DataConnection.AddOrUpdateBackupAndSchedule(data.Backup, data.Schedule);
    
                        }
                        
                        OutputObject(bw, new { status = "OK" });
                    }
                }
                catch (Exception ex)
                {
                    if (data == null)
                        ReportError(response, bw, string.Format("Unable to parse backup or schedule object: {0}", ex.Message));
                    else
                        ReportError(response, bw, string.Format("Unable to save backup or schedule: {0}", ex.Message));
                        
                }
            }
            
            private class AddOrUpdateBackupData
            {
                public Database.Schedule Schedule {get; set;}
                public Database.Backup Backup {get; set;}
            }

            private void AddBackup(HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session, BodyWriter bw)
            {
                var str = request.Form["data"].Value;
                if (string.IsNullOrWhiteSpace(str))
                {
                    ReportError(response, bw, "Missing backup object");
                    return;
                }

                AddOrUpdateBackupData data = null;
                try
                {
                    data = Serializer.Deserialize<AddOrUpdateBackupData>(new StringReader(str));
                    if (data.Backup == null)
                    {
                        ReportError(response, bw, "Data object had no backup entry");
                        return;
                    }
                        
                    data.Backup.ID = null;
                    
                    if (Duplicati.Library.Utility.Utility.ParseBool(request.Form["temporary"].Value, false))
                    {
                        Program.DataConnection.RegisterTemporaryBackup(data.Backup);
                        
                        OutputObject(bw, new { status = "OK", ID = data.Backup.ID });
                    }
                    else
                    {
                        lock(Program.DataConnection.m_lock)
                        {
                            if (Program.DataConnection.Backups.Where(x => x.Name.Equals(data.Backup.Name, StringComparison.InvariantCultureIgnoreCase)).Any())
                            {
                                ReportError(response, bw, "There already exists a backup with the name: " + data.Backup.Name);
                                return;
                            }
                            
                            Program.DataConnection.AddOrUpdateBackupAndSchedule(data.Backup, data.Schedule);
                        }
                        
                        OutputObject(bw, new { status = "OK", ID = data.Backup.ID });
                    }
                }
                catch (Exception ex)
                {
                    if (data == null)
                        ReportError(response, bw, string.Format("Unable to parse backup or schedule object: {0}", ex.Message));
                    else
                        ReportError(response, bw, string.Format("Unable to save schedule or backup object: {0}", ex.Message));
                }
            }

            private void DeleteBackup(HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session, BodyWriter bw)
            {
                HttpServer.HttpInput input = request.Method.ToUpper() == "POST" ? request.Form : request.QueryString;
                var backup = Program.DataConnection.GetBackup(input["id"].Value);
                if (backup == null)
                {
                    ReportError(response, bw, "Invalid or missing backup id");
                    return;
                }

                if (Program.WorkThread.Active)
                {
                    try
                    {
                        //TODO: It's not safe to access the values like this, 
                        //because the runner thread might interfere
                        var nt = Program.WorkThread.CurrentTask;
                        if (backup.Equals(nt == null ? null : nt.Backup))
                        {
                            bool force;
                            if (!bool.TryParse(input["force"].Value, out force))
                                force = false;
                            
                            if (!force)
                            {
                                OutputObject(bw, new { status = "failed", reason = "backup-in-progress" });
                                return;
                            }

                            bool hasPaused = Program.LiveControl.State == LiveControls.LiveControlState.Paused;
                            Program.LiveControl.Pause();

                            try
                            {
                                for (int i = 0; i < 10; i++)
                                    if (Program.WorkThread.Active)
                                    {
                                        var t = Program.WorkThread.CurrentTask;
                                        if (backup.Equals(t == null ? null : t.Backup))
                                            System.Threading.Thread.Sleep(1000);
                                        else
                                            break;
                                    }
                                    else
                                        break;
                            }
                            finally
                            {
                            }

                            if (Program.WorkThread.Active)
                            {
                                var t = Program.WorkThread.CurrentTask;
                                if (backup.Equals(t == null ? null : t.Backup))
                                {
                                    if (hasPaused)
                                        Program.LiveControl.Resume();
                                    OutputObject(bw, new { status = "failed", reason = "backup-unstoppable" });
                                    return;
                                }
                            }

                            if (hasPaused)
                                Program.LiveControl.Resume();
                        }
                    }
                    catch (Exception ex)
                    {
                        OutputObject(bw, new { status = "error", message = ex.Message });
                        return;
                    }
                }
                
                Program.DataConnection.DeleteBackup(backup);

                //We have fiddled with the schedules
                Program.Scheduler.Reschedule();

                OutputObject(bw, new { status = "OK" });
            }

            private void SendCommand(HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session, BodyWriter bw)
            {
                HttpServer.HttpInput input = request.Method.ToUpper() == "POST" ? request.Form : request.QueryString;

                string command = input["command"].Value ?? "";
                
                switch (command.ToLowerInvariant())
                {
                    case "pause":
                        if (input.Contains("duration") && !string.IsNullOrWhiteSpace(input["duration"].Value))
                        {
                            TimeSpan ts;
                            try
                            {
                                ts = Library.Utility.Timeparser.ParseTimeSpan(input["duration"].Value);
                            }
                            catch (Exception ex)
                            {
                                ReportError(response, bw, ex.Message);
                                return;
                            }
                            if (ts.TotalMilliseconds > 0)
                                Program.LiveControl.Pause(ts);
                            else
                                Program.LiveControl.Pause();
                        }
                        else
                        {
                            Program.LiveControl.Pause();
                        }

                        OutputObject(bw, new { Status = "OK" });
                        return;
                    case "resume":
                        Program.LiveControl.Resume();
                        OutputObject(bw, new { Status = "OK" });
                        return;

                    case "stop":
                    case "abort":
                        {
                            var task = Program.WorkThread.CurrentTask;
                            var tasks = Program.WorkThread.CurrentTasks;
                            long taskid;
                            if (!input.Contains("taskid") || !long.TryParse(input["taskid"].Value ?? "", out taskid))
                            {
                                ReportError(response, bw, "Invalid or missing taskid");
                                return;
                            }

                            if (task != null)
                                tasks.Insert(0, task);
                        
                            task = tasks.Where(x => x.TaskID == taskid).FirstOrDefault();
                            if (task == null)
                            {
                                ReportError(response, bw, "No such task");
                                return;
                            }
                            
                            if (string.Equals(command, "abort", StringComparison.InvariantCultureIgnoreCase))
                                task.Abort();
                            else
                                task.Stop();
                                                    
                            OutputObject(bw, new { Status = "OK" });
                            return;
                        }

                    case "is-backup-active":
                        {
                            var backup = Program.DataConnection.GetBackup(input["id"].Value);
                            if (backup == null)
                            {
                                ReportError(response, bw, string.Format("No backup found for id: {0}", input["id"].Value));
                                return;
                            }

                            var t = Program.WorkThread.CurrentTask;
                            var bt = t == null ? null : t.Backup;
                            if (bt != null && backup.ID == bt.ID)
                            {
                                OutputObject(bw, new { Status = "OK", Active = true });
                                return;
                            }
                            else if (Program.WorkThread.CurrentTasks.Where(x =>
                            { 
                                var bn = x == null ? null : x.Backup;
                                return bn == null || bn.ID == backup.ID;
                            }).Any())
                            {
                                OutputObject(bw, new { Status = "OK", Active = true });
                                return;
                            }
                            else
                            {
                                OutputObject(bw, new { Status = "OK", Active = false });
                                return;
                            }
                        }

                    case "run":
                    case "run-backup":
                        {
                            
                            var backup = Program.DataConnection.GetBackup(input["id"].Value);
                            if (backup == null)
                            {
                                ReportError(response, bw, string.Format("No backup found for id: {0}", input["id"].Value));
                                return;
                            }

                            var t = Program.WorkThread.CurrentTask;
                            var bt = t == null ? null : t.Backup;
                            if (bt != null && backup.ID == bt.ID)
                            {
                                // Already running
                            }
                            else if (Program.WorkThread.CurrentTasks.Where(x => { 
                                var bn = x == null ? null : x.Backup;
                                return bn == null || bn.ID == backup.ID;
                                }).Any())
                            {
                                // Already in queue
                            }
                            else
                            {
                                Program.WorkThread.AddTask(Runner.CreateTask(DuplicatiOperation.Backup, backup));
                                Program.StatusEventNotifyer.SignalNewEvent();
                            }
                        }
                        OutputObject(bw, new { Status = "OK" });
                        return;

                    case "run-verify":
                        {
                            var backup = Program.DataConnection.GetBackup(input["id"].Value);
                            if (backup == null)
                            {
                                ReportError(response, bw, string.Format("No backup found for id: {0}", input["id"].Value));
                                return;
                            }

                            Program.WorkThread.AddTask(Runner.CreateTask(DuplicatiOperation.Verify, backup));
                            Program.StatusEventNotifyer.SignalNewEvent();
                        }
                        OutputObject(bw, new { Status = "OK" });
                        return;

                    case "run-repair":
                        {
                            var backup = Program.DataConnection.GetBackup(input["id"].Value);
                            if (backup == null)
                            {
                                ReportError(response, bw, string.Format("No backup found for id: {0}", input["id"].Value));
                                return;
                            }

                            Program.WorkThread.AddTask(Runner.CreateTask(DuplicatiOperation.Repair, backup));
                            Program.StatusEventNotifyer.SignalNewEvent();
                        }
                        OutputObject(bw, new { Status = "OK" });
                        return;
                    case "create-report":
                        {
                            var backup = Program.DataConnection.GetBackup(input["id"].Value);
                            if (backup == null)
                            {
                                ReportError(response, bw, string.Format("No backup found for id: {0}", input["id"].Value));
                                return;
                            }

                            Program.WorkThread.AddTask(Runner.CreateTask(DuplicatiOperation.CreateReport, backup));
                            Program.StatusEventNotifyer.SignalNewEvent();
                        }
                        OutputObject(bw, new { Status = "OK" });
                        return;

                    case "clear-warning":
                        Program.HasWarning = false;
                        Program.StatusEventNotifyer.SignalNewEvent();
                        OutputObject(bw, new { Status = "OK" });
                        return;
                    case "clear-error":
                        Program.HasError = false;
                        Program.StatusEventNotifyer.SignalNewEvent();
                        OutputObject(bw, new { Status = "OK" });
                        return;
                    
                    default:
                        
                        var m = Duplicati.Library.DynamicLoader.WebLoader.Modules.Where(x => x.Key.Equals(command, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
                        if (m == null)
                        {
                            ReportError(response, bw, string.Format("Unsupported command {0}", command));
                            return;
                        }
                        
                        OutputObject(bw, new { 
                            Status = "OK", 
                            Result = m.Execute(input.Where(x => 
                                    !x.Name.Equals("command", StringComparison.InvariantCultureIgnoreCase)
                                    &&
                                    !x.Name.Equals("action", StringComparison.InvariantCultureIgnoreCase)
                                ).ToDictionary(x => x.Name, x => x.Value))
                        });
                        return;
                }
            }
            
        }
    }
}
