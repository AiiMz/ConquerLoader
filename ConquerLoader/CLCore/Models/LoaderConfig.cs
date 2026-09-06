using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace CLCore.Models
{
    public class LoaderConfig
    {
        public BindingList<ServerConfiguration> Servers { get; set; }
        public ServerConfiguration DefaultServer { get; set; }
        public bool DebugMode { get; set; }
        public bool CloseOnFinish { get; set; }
        public bool HighResolution { get; set; }
        public bool FHDResolution { get; set; }
        public bool FullScreen { get; set; }
        public bool ServernameChange { get; set; }
        public bool DisableAutoFixFlash { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Lang { get; set; }
        public string LicenseKey { get; set; }
        public bool DisableScreenChanges { get; set; }
        public bool UseCustomDLLs { get; set; }
        public bool FPSUnlock { get; set; }

        /// <summary>
        /// Stop the client drawing wings. A player preference rather than a
        /// server setting: it rewrites ini\Action3DEffect.ini before launch and
        /// the server is never told, so the wings stay equipped and keep every
        /// point of battle power they grant. See CLCore.ClientOptions.WingVisibility.
        /// </summary>
        public bool HideWings { get; set; }

        public LoaderConfig()
        {
            if (Servers == null)
            {
                Servers = new BindingList<ServerConfiguration>();
            }
        }

        public List<ServerDatGroup> GetGroups()
        {
            List<ServerDatGroup> Groups = new List<ServerDatGroup>();
            foreach (ServerConfiguration Server in Servers)
            {
                ServerDatGroup g = Groups.Where(x => x.GroupName == Server.Group.GroupName && x.GroupIcon == Server.Group.GroupIcon).FirstOrDefault();
                if (g != null)
                {
                    g.Servers.Add(Server);
                } else
                {
                    if (Server.Group.Servers == null)
                    {
                        Server.Group.Servers = new List<ServerConfiguration>();
                    }
                    Server.Group.Servers.Add(Server);
                    Groups.Add(Server.Group);
                }
            }
            return Groups;
        }
    }
}
