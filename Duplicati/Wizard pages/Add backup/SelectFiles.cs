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
using System.ComponentModel;
using System.Drawing;
using System.Data;
using System.Text;
using System.Windows.Forms;
using System.Windows.Forms.Wizard;
using Duplicati.Datamodel;

namespace Duplicati.Wizard_pages.Add_backup
{
    public partial class SelectFiles : UserControl, IWizardControl, Interfaces.IScheduleBased
    {
        IWizardForm m_owner;
        private WorkerThread<string> m_calculator;
        private object m_lock = new object();
        private Dictionary<string, long> m_sizes;
        private Schedule m_schedule;

        private string m_myPictures;
        private string m_myMusic;
        private string m_desktop;
        private string m_appData;
        private string m_myDocuments;

        public SelectFiles()
        {
            InitializeComponent();
            m_sizes = new Dictionary<string, long>();
            m_calculator = new WorkerThread<string>(CalculateFolderSize);
            m_calculator.CompletedWork += new EventHandler(m_calculator_CompletedWork);

            m_myPictures = System.Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            m_myMusic = System.Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            m_desktop = System.Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            m_appData = System.Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            m_myDocuments = System.Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        void m_calculator_CompletedWork(object sender, EventArgs e)
        {
            if (this.InvokeRequired)
                this.Invoke(new EventHandler(m_calculator_CompletedWork), sender, e);
            else
            {
                long s = 0;

                if (DocumentsRadio.Checked)
                    foreach (long l in m_sizes.Values)
                        s += l;
                else
                    s = m_sizes.ContainsKey(TargetFolder.Text) ? m_sizes[TargetFolder.Text] : 0;

                totalSize.Text = string.Format("The selected items take up {0} of space", Duplicati.Datamodel.Utillity.FormatSizeString(s));

                if (m_sizes.ContainsKey(m_myMusic))
                    myMusicSize.Text = Duplicati.Datamodel.Utillity.FormatSizeString(m_sizes[m_myMusic]);
                if (m_sizes.ContainsKey(m_myPictures))
                    myPicturesSize.Text = Duplicati.Datamodel.Utillity.FormatSizeString(m_sizes[m_myPictures]);
                if (m_sizes.ContainsKey(m_desktop))
                    desktopSize.Text = Duplicati.Datamodel.Utillity.FormatSizeString(m_sizes[m_desktop]);
                if (m_sizes.ContainsKey(m_appData))
                    appdataSize.Text = Duplicati.Datamodel.Utillity.FormatSizeString(m_sizes[m_appData]);

                //TODO: Do not count myMusic and myPictures, if they are inside myDocuments
                if (m_sizes.ContainsKey(m_myDocuments))
                    myMusicSize.Text = Duplicati.Datamodel.Utillity.FormatSizeString(m_sizes[m_myDocuments]);

                if (m_sizes.ContainsKey(TargetFolder.Text))
                    customSize.Text = Duplicati.Datamodel.Utillity.FormatSizeString(m_sizes[TargetFolder.Text]);


            }
        }

        #region IWizardControl Members

        Control IWizardControl.Control
        {
            get { return this; }
        }

        string IWizardControl.Title
        {
            get { return "Select files to backup"; }
        }

        string IWizardControl.HelpText
        {
            get { return "On this page you must select the folder and files you wish to backup"; }
        }

        Image IWizardControl.Image
        {
            get { return null; }
        }

        bool IWizardControl.FullSize
        {
            get { return false; }
        }

        void IWizardControl.Enter(IWizardForm owner)
        {
            m_owner = owner;
            if (m_schedule != null)
                TargetFolder.Text = m_schedule.Tasks[0].SourcePath;
            Rescan();
        }

        void IWizardControl.Leave(IWizardForm owner, ref bool cancel)
        {
            if (DocumentsRadio.Checked)
            {
                MessageBox.Show(this, "This feature is not ready. Please manually point out the folder to back up.", Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                cancel = true;
                return;
            }
            else
            {
                if (!System.IO.Directory.Exists(TargetFolder.Text))
                {
                    MessageBox.Show(this, "The folder \"" + TargetFolder.Text + "\" does not exist.", Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    cancel = true;
                    return;
                }
            }
            m_calculator.ClearQueue(true);
        }

        #endregion

        private void Rescan()
        {
            lock (m_lock)
            {
                if (!m_sizes.ContainsKey(m_myMusic))
                    m_calculator.AddTask(m_myMusic);
                if (!m_sizes.ContainsKey(m_myPictures))
                    m_calculator.AddTask(m_myPictures);
                if (!m_sizes.ContainsKey(m_desktop))
                    m_calculator.AddTask(m_desktop);
                if (!m_sizes.ContainsKey(m_appData))
                    m_calculator.AddTask(m_appData);
                if (!m_sizes.ContainsKey(m_myDocuments))
                    m_calculator.AddTask(m_myDocuments);
            }
        }

        private void BrowseFolderButton_Click(object sender, EventArgs e)
        {
            if (folderBrowserDialog.ShowDialog(this) == DialogResult.OK)
            {
                TargetFolder.Text = folderBrowserDialog.SelectedPath;
                TargetFolder_Leave(null, null);
            }
        }

        private void TargetType_CheckedChanged(object sender, EventArgs e)
        {
            DocumentGroup.Enabled = DocumentsRadio.Checked;
            FolderGroup.Enabled = FolderRadio.Checked;
            if (DocumentsRadio.Checked)
                Rescan();
        }

        private void CalculateFolderSize(string folder)
        {
            lock (m_lock)
                if (m_sizes.ContainsKey(folder))
                    return;

            long size = 0;
            if (System.IO.Directory.Exists(folder))
            {
                Queue<string> work = new Queue<string>();
                work.Enqueue(folder);
                try
                {
                    while(work.Count > 0)
                        try
                        {
                            string f = work.Dequeue();
                            foreach (string s in System.IO.Directory.GetDirectories(f))
                                work.Enqueue(s);
                            foreach (string s in System.IO.Directory.GetFiles(f))
                                try { size += new System.IO.FileInfo(s).Length; }
                                catch { }
                        }
                        catch
                        {
                        }
                }
                catch
                {
                }
            }

            lock (m_lock)
                m_sizes[folder] = size;
        }

        private void TargetFolder_Leave(object sender, EventArgs e)
        {
            lock(m_lock)
                if (TargetFolder.Text.Trim().Length > 0 && !m_sizes.ContainsKey(TargetFolder.Text))
                {
                    m_calculator.ClearQueue(true);
                    m_calculator.AddTask(TargetFolder.Text);
                }

            m_schedule.Tasks[0].SourcePath = TargetFolder.Text;
        }

        #region IScheduleBased Members

        public void Setup(Duplicati.Datamodel.Schedule schedule)
        {
            m_schedule = schedule;
        }

        #endregion
    }
}