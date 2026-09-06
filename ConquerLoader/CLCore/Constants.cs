using System.Windows.Forms;

namespace CLCore
{
	public static class Constants
	{
		//The folder name which contains the plugin DLLs
		public const string PluginsFolderName = "Plugins";
		public static string LicenseKey = null;
		public static string ClientPath = null;
		public static System.ComponentModel.BackgroundWorker MainWorker = null;
		public static bool CloseOnFinish = false;
		public static bool HideInTrayOnFinish = false;
		public static int MinVersionCreateFlashFix = 5717;
        public static int MinVersionUseRAWServerDat = 5095;
        public static int MaxVersionUseRAWServerDat = 6736;
		public static string LockConfigurationKey = "CONQUERLOADERDFX";
		public static int MinVersionUseDX8DX9Folders = 6371;
		// Disabled. CLServer answers "does this IP have a live loader connection?",
		// which is meant to catch players running conquer.exe directly or with a bot.
		// It cannot: the socket carries no token and is never correlated with a game
		// session, so any TCP connect to port 8000 from the same address satisfies it,
		// and one connection whitelists everyone behind the same NAT. The connection
		// list also round-trips through a third-party API keyed by a license key that
		// ships hardcoded, so operators using it share and overwrite each other's data.
		// Nothing here consumes CheckConnectionByIP, so this only ever cost a socket.
		public static bool EnableCLServerConnections = false;
        public static bool ForceServerDat = false;
    }
	public static class CLTheme
	{
        public static Control.ControlCollection MainControls = null;
        public static object MainForm = null;
    }
	public enum PluginType
	{
		FREE,
		PREMIUM
	}
}

