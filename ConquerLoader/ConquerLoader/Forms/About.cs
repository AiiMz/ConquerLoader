using System;
using System.Diagnostics;

namespace ConquerLoader.Forms
{
    public partial class About : MetroFramework.Forms.MetroForm
    {
        public About()
        {
            InitializeComponent();
        }

        private void About_Load(object sender, EventArgs e)
        {
            System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
            System.Diagnostics.FileVersionInfo fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location);
            lblAboutVersionN.Text = "v" + fvi.ProductVersion;
        }

        private void LblAboutChangelog_LinkClicked(object sender, System.Windows.Forms.LinkLabelLinkClickedEventArgs e)
        {
            Process.Start("https://conquerloader.com/changelog/");
        }
    }
}
