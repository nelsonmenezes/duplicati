#region Disclaimer / License
// Copyright (C) 2008, Kenneth Skovhede
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

namespace Duplicati.Library.Main
{
    public static class Interface
    {
        public static string Backup(string source, string target, Dictionary<string, string> options)
        {
            //TODO: Rethink how the encryption is used, the hidden method prevents the actual size from being recorded
            BackupStatistics bs = new BackupStatistics();

            SetupCommonOptions(options);

            using (new Logging.Timer("Backup from " + source + " to " + target))
            {
                FilenameStrategy fns = new FilenameStrategy(options);
                Core.FilenameFilter filter = new Duplicati.Library.Core.FilenameFilter(options);
                bool full = options.ContainsKey("full");

                Backend.IBackendInterface backend = Encryption.EncryptedBackendWrapper.WrapWithEncryption(Backend.BackendLoader.GetBackend(target, options), options);
                List<BackupEntry> prev = ParseFileList(target, options);
                bs.NumberOfRemoteCalls++;

                if (prev.Count == 0)
                    full = true;

                if (!full && options.ContainsKey("full-if-older-than"))
                    full = DateTime.Now > Core.Timeparser.ParseTimeInterval(options["full-if-older-than"], prev[prev.Count - 1].Time);

                using (Core.TempFolder basefolder = new Duplicati.Library.Core.TempFolder())
                {
                    if (!full)
                    {
                        using (new Logging.Timer("Reading incremental data"))
                        {
                            foreach (BackupEntry be in prev[prev.Count - 1].SignatureFile) 
                                using (Core.TempFile t = new Duplicati.Library.Core.TempFile())
                                {
                                    using (new Logging.Timer("Get " + be.Filename))
                                        backend.Get(be.Filename, t);
                                    Compression.Compression.Decompress(t, basefolder);
                                    
                                    bs.NumberOfRemoteCalls++;
                                    bs.NumberOfBytesDownloaded += new System.IO.FileInfo(t).Length;
                                }

                            foreach (BackupEntry be in prev[prev.Count - 1].Incrementals)
                                foreach (BackupEntry bes in be.SignatureFile) 
                                    using (Core.TempFile t = new Duplicati.Library.Core.TempFile())
                                    {
                                        using (new Logging.Timer("Get " + bes.Filename))
                                            backend.Get(bes.Filename, t);

                                        bs.NumberOfRemoteCalls++;
                                        bs.NumberOfBytesDownloaded += new System.IO.FileInfo(t).Length;

                                        using (Core.TempFolder tf = new Duplicati.Library.Core.TempFolder())
                                        {
                                            Compression.Compression.Decompress(t, tf);
                                            using (new Logging.Timer("Full signature merge"))
                                                Main.RSync.RSyncDir.MergeSignatures(basefolder, tf);
                                        }
                                    }
                        }
                    }
                    DateTime backuptime = DateTime.Now;

                    using (RSync.RSyncDir dir = new Duplicati.Library.Main.RSync.RSyncDir(source, basefolder))
                    {

                        using (new Logging.Timer("Initiating multipass"))
                            dir.InitiateMultiPassDiff(full, filter);

                        int volumesize = (int)Core.Sizeparser.ParseSize(options.ContainsKey("volsize") ? options["volsize"] : "5", "mb");
                        volumesize = Math.Max(1024 * 1024, volumesize);

                        long totalmax = options.ContainsKey("totalsize") ? Core.Sizeparser.ParseSize(options["totalsize"], "mb") : long.MaxValue;
                        totalmax = Math.Max(volumesize, totalmax);

                        List<string> folders = dir.EnumerateSourceFolders();

                        int vol = 0;
                        long totalsize = 0;

                        List<string> contenthashes = new List<string>();
                        List<string> signaturehashes = new List<string>();

                        bool done = false;
                        while (!done && totalsize < totalmax)
                        {
                            using (new Logging.Timer("Multipass " + (vol + 1).ToString()))
                            {
                                using (Core.TempFile sigzip = new Duplicati.Library.Core.TempFile())
                                {
                                    using (Compression.Compression signature = new Duplicati.Library.Compression.Compression(dir.m_targetfolder, sigzip))
                                    {
                                        if (folders != null)
                                            foreach (string s in folders)
                                                signature.AddFolder(s);

                                        folders = null;

                                        using (Core.TempFile zf = new Duplicati.Library.Core.TempFile())
                                        {
                                            done = dir.MakeMultiPassDiff(signature, zf, volumesize);
                                            totalsize += new System.IO.FileInfo(zf).Length;
                                            contenthashes.Add(Core.Utility.CalculateHash(zf));
                                            using (new Logging.Timer("Writing delta file " + (vol + 1).ToString()))
                                                backend.Put(fns.GenerateFilename(BackupEntry.EntryType.Content, full, backuptime, vol + 1) + ".zip", zf);

                                            bs.NumberOfRemoteCalls++;
                                            bs.NumberOfBytesUploaded += new System.IO.FileInfo(zf).Length;
                                        }
                                    }

                                    totalsize += new System.IO.FileInfo(sigzip).Length;
                                    signaturehashes.Add(Core.Utility.CalculateHash(sigzip));

                                    using (new Logging.Timer("Writing remote signatures"))
                                        backend.Put(fns.GenerateFilename(BackupEntry.EntryType.Signature, full, backuptime, vol + 1) + ".zip", sigzip);

                                    bs.NumberOfRemoteCalls++;
                                    bs.NumberOfBytesUploaded += new System.IO.FileInfo(sigzip).Length;
                                }

                                using (Core.TempFile mf = new Duplicati.Library.Core.TempFile())
                                using (new Logging.Timer("Writing manifest"))
                                {
                                    System.Xml.XmlDocument doc = new System.Xml.XmlDocument();
                                    System.Xml.XmlNode root = doc.AppendChild(doc.CreateElement("Manifest"));
                                    root.Attributes.Append(doc.CreateAttribute("version")).Value = "1";
                                    root.AppendChild(doc.CreateElement("VolumeCount")).InnerText = (vol + 1).ToString();
                                    System.Xml.XmlNode contentroot = root.AppendChild(doc.CreateElement("ContentFiles"));
                                    System.Xml.XmlNode signatureroot = root.AppendChild(doc.CreateElement("SignatureFiles"));
                                    
                                    foreach (string s in contenthashes)
                                        contentroot.AppendChild(doc.CreateElement("Hash")).InnerText = s;
                                    foreach (string s in signaturehashes)
                                        signatureroot.AppendChild(doc.CreateElement("Hash")).InnerText = s;

                                    //TODO: Actually read the manifest on restore
                                    doc.Save(mf);

                                    backend.Put(fns.GenerateFilename(BackupEntry.EntryType.Manifest, full, backuptime) + ".manifest", mf);

                                    bs.NumberOfRemoteCalls++;
                                    bs.NumberOfBytesUploaded += new System.IO.FileInfo(mf).Length;
                                }

                                vol++;

                            }
                        }

                        bs.DeletedFiles = dir.DeletedFiles;
                        bs.DeletedFolders = dir.DeletedFolders;
                        bs.ModifiedFiles = dir.ModifiedFiles;
                        bs.AddedFiles = dir.AddedFiles;
                        bs.ExaminedFiles = dir.ExaminedFiles;
                        bs.SizeOfModifiedFiles = dir.ModifiedFilesSize;
                        bs.SizeOfAddedFiles = dir.AddedFilesSize;
                        bs.SizeOfExaminedFiles = dir.ExaminedFilesSize;
                    }
                }



            }

            bs.EndTime = DateTime.Now;
            return bs.ToString();
        }

        public static string Restore(string source, string target, Dictionary<string, string> options)
        {
            SetupCommonOptions(options);
            RestoreStatistics rs = new RestoreStatistics();

            using (new Logging.Timer("Restore from " + source + " to " + target))
            {
                string specificfile = options.ContainsKey("file-to-restore") ? options["file-to-restore"] : "";
                string specifictime = options.ContainsKey("restore-time") ? options["restore-time"] : "now";
                Backend.IBackendInterface backend = Backend.BackendLoader.GetBackend(source, options);

                if (string.IsNullOrEmpty(specifictime))
                    specifictime = "now";

                List<BackupEntry> backups = ParseFileList(source, options);
                if (backups.Count == 0)
                    throw new Exception("No backups found at remote location");
                rs.RemoteCalls++;

                DateTime timelimit = Core.Timeparser.ParseTimeInterval(specifictime, DateTime.Now);

                BackupEntry bestFit = backups[0];
                List<BackupEntry> additions = new List<BackupEntry>();
                foreach (BackupEntry be in backups)
                    if (be.Time < timelimit)
                    {
                        bestFit = be;
                        foreach (BackupEntry bex in be.Incrementals)
                            if (bex.Time <= timelimit)
                                additions.Add(bex);

                    }

                if (bestFit.SignatureFile.Count == 0 || bestFit.ContentVolumes.Count == 0)
                    throw new Exception("Unable to parse filenames for the desired volumes");

                if (bestFit.EncryptionMode != null)
                {
                    //TODO: Use mode to determine the actual instance to use
                    if (bestFit.EncryptionMode != "aes")
                        throw new Exception("Unexpected encryption mode");

                    backend = Encryption.EncryptedBackendWrapper.WrapWithEncryption(backend, options);
                }

                using (Core.TempFolder basefolder = new Duplicati.Library.Core.TempFolder())
                {
                    foreach(BackupEntry be in bestFit.SignatureFile)
                        using (Core.TempFile basezip = new Duplicati.Library.Core.TempFile())
                        {
                            if (be.CompressionMode != "zip")
                                throw new Exception("Unexpected compression mode");

                            using (new Logging.Timer("Get " + be.Filename))
                                backend.Get(be.Filename, basezip);

                            rs.RemoteCalls++;
                            rs.BytesDownloaded += new System.IO.FileInfo(basezip).Length;
                            Compression.Compression.Decompress(basezip, basefolder);
                        }

                    foreach (BackupEntry vol in bestFit.ContentVolumes)
                        using (Core.TempFile basezip = new Duplicati.Library.Core.TempFile())
                        {
                            if (vol.CompressionMode != "zip")
                                throw new Exception("Unexpected compression mode");

                            using (new Logging.Timer("Get " + vol.Filename))
                                backend.Get(vol.Filename, basezip);

                            rs.RemoteCalls++;
                            rs.BytesDownloaded += new System.IO.FileInfo(basezip).Length;
                            
                            Compression.Compression.Decompress(basezip, basefolder);
                        }

                    using (RSync.RSyncDir sync = new Duplicati.Library.Main.RSync.RSyncDir(target, basefolder))
                    {

                        using (new Logging.Timer("Full restore to " + target))
                            sync.Restore(target, new List<string>());

                        foreach (BackupEntry p in additions)
                        {
                            if (p.SignatureFile.Count == 0 || p.ContentVolumes.Count == 0)
                                throw new Exception("Unable to parse filenames for the volume: " + p.Filename);

                            using (Core.TempFolder t = new Duplicati.Library.Core.TempFolder())
                            {
                                foreach (BackupEntry be in p.SignatureFile)
                                    using (Core.TempFile patchzip = new Duplicati.Library.Core.TempFile())
                                    {
                                        if (be.CompressionMode != "zip")
                                            throw new Exception("Unexpected compression mode");

                                        using (new Logging.Timer("Get " + be.Filename))
                                            backend.Get(be.Filename, patchzip);

                                        rs.RemoteCalls++;
                                        rs.BytesDownloaded += new System.IO.FileInfo(patchzip).Length;
                                        
                                        Compression.Compression.Decompress(patchzip, t);
                                    }

                                foreach (BackupEntry vol in p.ContentVolumes)
                                    using (Core.TempFile patchzip = new Duplicati.Library.Core.TempFile())
                                    {
                                        if (vol.CompressionMode != "zip")
                                            throw new Exception("Unexpected compression mode");

                                        using (new Logging.Timer("Get " + vol.Filename))
                                            backend.Get(vol.Filename, patchzip);
                                        
                                        rs.RemoteCalls++;
                                        rs.BytesDownloaded += new System.IO.FileInfo(patchzip).Length;

                                        Compression.Compression.Decompress(patchzip, t);
                                    }

                                using (new Logging.Timer("Incremental patch " + p.Time.ToString()))
                                    sync.Patch(target, t);

                            }
                        }
                    }

                }
            }

            rs.EndTime = DateTime.Now;

            //TODO: The RS should have the number of restored files, and the size of those

            return rs.ToString();
        }

        public static string RemoveAllButNFull(string source, Dictionary<string, string> options)
        {
            if (!options.ContainsKey("remove-all-but-n-full"))
                throw new Exception("No count given for \"Remove All But N Full\"");

            int x = int.Parse(options["remove-all-but-n-full"]);
            List<BackupEntry> entries = ParseFileList(source, options);

            Backend.IBackendInterface backend = Backend.BackendLoader.GetBackend(source, options);

            List<BackupEntry> toremove = new List<BackupEntry>();

            while (entries.Count > x)
            {
                BackupEntry be = entries[0];
                entries.RemoveAt(0);

                be.Incrementals.Reverse();
                toremove.AddRange(be.Incrementals);
                toremove.Add(be);
            }

            return RemoveBackupSets(backend, options, toremove);
        }

        public static string RemoveOlderThan(string source, Dictionary<string, string> options)
        {
            StringBuilder sb = new StringBuilder();

            if (!options.ContainsKey("remove-older-than"))
                throw new Exception("No count given for \"Remove Older Than\"");

            string duration = options["remove-older-than"];
            List<BackupEntry> entries = ParseFileList(source, options);

            Backend.IBackendInterface backend = Backend.BackendLoader.GetBackend(source, options);
            DateTime expires = Core.Timeparser.ParseTimeInterval(duration, DateTime.Now, true);

            List<BackupEntry> toremove = new List<BackupEntry>();

            while (entries.Count > 0 && entries[0].Time > expires)
            {
                BackupEntry be = entries[0];
                entries.RemoveAt(0);

                bool hasNewer = false;
                foreach (BackupEntry bex in be.Incrementals)
                    if (bex.Time < expires)
                    {
                        hasNewer = true;
                        break;
                    }

                if (hasNewer)
                {
                    List<BackupEntry> t = new List<BackupEntry>(be.Incrementals);
                    t.Insert(0, be);

                    for (int i = 0; i < t.Count; i++)
                        if (t[i].Time > expires)
                            sb.AppendLine("Not deleting backup at time: " + t[i].Time.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", because later backups depend on it");

                    break;
                }
                else
                {
                    be.Incrementals.Reverse();
                    toremove.AddRange(be.Incrementals);
                    toremove.Add(be);
                }
            }


            sb.Append(RemoveBackupSets(backend, options, toremove));
            
            return sb.ToString();
        }

        private static string RemoveBackupSets(Backend.IBackendInterface backend, Dictionary<string, string> options, List<BackupEntry> entries)
        {
            StringBuilder sb = new StringBuilder();

            foreach (BackupEntry be in entries)
            {
                sb.AppendLine("Deleting backup at " + be.Time.ToString(System.Globalization.CultureInfo.InvariantCulture));

                if (options.ContainsKey("force"))
                {
                    //Delete manifest
                    backend.Delete(be.Filename);

                    foreach (BackupEntry bex in be.ContentVolumes)
                        backend.Delete(bex.Filename);

                    foreach (BackupEntry bex in be.SignatureFile)
                        backend.Delete(bex.Filename);
                }
            }

            if (!options.ContainsKey("force"))
                sb.AppendLine("Files are not deleted, use the --force command to actually remove files");

            return sb.ToString();
        }


        public static string[] List(string source, Dictionary<string, string> options)
        {
            SetupCommonOptions(options);

            List<string> res = new List<string>();
            Duplicati.Library.Backend.IBackendInterface i = new Duplicati.Library.Backend.BackendLoader(source, options);
            foreach (Duplicati.Library.Backend.FileEntry fe in i.List())
                res.Add(fe.Name);

            return res.ToArray();
        }

        public static List<BackupEntry> ParseFileList(string source, Dictionary<string, string> options)
        {
            SetupCommonOptions(options);

            using (new Logging.Timer("Getting and sorting filelist from " + source))
            {
                FilenameStrategy fns = new FilenameStrategy(options);

                List<BackupEntry> incrementals = new List<BackupEntry>();
                List<BackupEntry> fulls = new List<BackupEntry>();
                Dictionary<string, List<BackupEntry>> signatures = new Dictionary<string, List<BackupEntry>>();
                Dictionary<string, List<BackupEntry>> contents = new Dictionary<string, List<BackupEntry>>();

                Duplicati.Library.Backend.IBackendInterface i = new Duplicati.Library.Backend.BackendLoader(source, options);

                foreach (Duplicati.Library.Backend.FileEntry fe in i.List())
                {
                    BackupEntry be = fns.DecodeFilename(fe);
                    if (be == null)
                        continue;

                    if (be.Type == BackupEntry.EntryType.Content)
                    {
                        string content = fns.GenerateFilename(BackupEntry.EntryType.Manifest, be.IsFull, be.IsShortName, be.Time) + ".manifest";
                        if (be.EncryptionMode != null)
                            content += "." + be.EncryptionMode;
                        
                        if (!contents.ContainsKey(content))
                            contents[content] = new List<BackupEntry>();
                        contents[content].Add(be);
                    }
                    else if (be.Type == BackupEntry.EntryType.Signature)
                    {
                        string content = fns.GenerateFilename(BackupEntry.EntryType.Manifest, be.IsFull, be.IsShortName, be.Time) + ".manifest";
                        if (be.EncryptionMode != null)
                            content += "." + be.EncryptionMode;

                        if (!signatures.ContainsKey(content))
                            signatures[content] = new List<BackupEntry>();
                        signatures[content].Add(be);
                    }
                    else if (be.Type != BackupEntry.EntryType.Manifest)
                        throw new Exception("Invalid entry type");
                    else if (be.IsFull)
                        fulls.Add(be);
                    else
                        incrementals.Add(be);
                }

                fulls.Sort(new Sorter());
                incrementals.Sort(new Sorter());

                foreach (BackupEntry be in fulls)
                {
                    if (contents.ContainsKey(be.Filename))
                        be.ContentVolumes.AddRange(contents[be.Filename]);
                    if (signatures.ContainsKey(be.Filename))
                        be.SignatureFile.AddRange(signatures[be.Filename]);
                }

                int index = 0;
                foreach (BackupEntry be in incrementals)
                {
                    if (contents.ContainsKey(be.Filename))
                        be.ContentVolumes.AddRange(contents[be.Filename]);
                    if (signatures.ContainsKey(be.Filename))
                        be.SignatureFile.AddRange(signatures[be.Filename]);

                    if (be.Time <= fulls[index].Time)
                    {
                        Logging.Log.WriteMessage("Failed to match incremental package to a full: " + be.Filename, Duplicati.Library.Logging.LogMessageType.Warning);
                        continue;
                    }
                    else
                    {
                        while (index < fulls.Count - 1 && be.Time > fulls[index].Time)
                            index++;
                        fulls[index].Incrementals.Add(be);
                    }
                }

                return fulls;
            }
        }


        private static void SetupCommonOptions(Dictionary<string, string> options)
        {
            if (options.ContainsKey("tempdir"))
                Core.TempFolder.SystemTempPath = options["tempdir"];
        }
    }
}