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

namespace Duplicati.Main
{
    internal class BackupEntry
    {
        private Duplicati.Backend.FileEntry m_fileentry;
        private DateTime m_time;
        private List<BackupEntry> m_incrementals;
        private bool m_isContent;
        private bool m_isFull;

        public string Filename { get { return m_fileentry.Name; } }
        public Backend.FileEntry FileEntry { get { return m_fileentry; } }
        public DateTime Time { get { return m_time; } }
        public bool IsContent { get { return m_isContent; } }
        public bool IsFull { get { return m_isFull; } }
        public List<BackupEntry> Incrementals { get { return m_incrementals; } }

        public BackupEntry(Backend.FileEntry fe, DateTime time, bool isContent, bool isFull)
        {
            m_fileentry = fe;
            m_time = time;
            m_isContent = isContent;
            m_isFull = isFull;
            m_incrementals = new List<BackupEntry>();
        }
    }

    internal class Sorter : IComparer<BackupEntry>
    {
        #region IComparer<BackupEntry> Members

        public int Compare(BackupEntry x, BackupEntry y)
        {
            return x.Time.CompareTo(y.Time);
        }

        #endregion
    }

}