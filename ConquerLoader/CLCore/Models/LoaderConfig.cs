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
        /// <summary>
        /// Leaves the client uncapped. Until now this stored a preference the
        /// loader never acted on; it is now the switch that turns
        /// <see cref="FpsLimit"/> off. See CLCore.ClientOptions.FrameRateLimit.
        /// </summary>
        public bool FPSUnlock { get; set; }

        /// <summary>
        /// Frames per second to cap the client at while <see cref="FPSUnlock"/>
        /// is off. Absent from a config.json - which is what every one written
        /// before this existed looks like - means the default of 60, not
        /// uncapped, because the client draws animations a step per frame and
        /// uncapped is what makes them run fast.
        /// </summary>
        public int FpsLimit { get; set; }

        /// <summary>
        /// Makes the hook write a `CLHook.fps.log` next to conquer.exe saying
        /// how often the client actually presented a frame. There is no UI for
        /// it and it is off unless a config.json says otherwise, because it is
        /// for answering one specific question: the frame count the client
        /// draws on screen is its own, and when it disagrees with the cap this
        /// is the only thing that can say which of the two is wrong.
        /// </summary>
        public bool FpsDebug { get; set; }

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
