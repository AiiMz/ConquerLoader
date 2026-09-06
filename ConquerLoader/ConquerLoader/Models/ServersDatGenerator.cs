using CLCore;
using CLCore.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ConquerLoader.Models
{
    public class ServersDatGenerator
    {
        private readonly string _Template = "";
        private BindingList<ServerConfiguration> _Servers { get; set; }
        private LoaderConfig Config { get; set; }
        private ServerConfiguration SelectedServer { get; set; }

        public ServersDatGenerator(BindingList<ServerConfiguration> Servers)
        {
            try
            {
                Config = Core.GetLoaderConfig();
                _Servers = Servers;
                if (Config.DefaultServer.ServerVersion >= 5717 && Config.DefaultServer.ServerVersion <= 6021)
                {
                    _Template = Properties.Resources.ServersXML_5717;
                }
                else
                {
                    _Template = Properties.Resources.ServersXML;
                }
                XDocument doc = XDocument.Parse(_Template);
                int initialId = 1;
                XElement tableRowsBase = doc.Element("mysqldump").Element("database").Element("table_data");
                XElement elGroup = tableRowsBase.Elements("row").Where(x => x.Elements("field").Where(y => y.Attribute("name").Value == "id" && y.Value == "0").Count() > 0).FirstOrDefault();
                List<ServerDatGroup> Groups = Config.GetGroups();
                elGroup.Elements("field").Where(x => x.Attribute("name").Value == "Child").FirstOrDefault().Value = Groups.Count.ToString();
                uint GroupId = 1;
                List<XElement> groupElements = new List<XElement>();
                List<XElement> serverElements = new List<XElement>();
                foreach (ServerDatGroup group in Groups)
                {
                    XElement rootElement = new XElement("row");
                    try
                    {
                        rootElement.Add(new XElement("field",
                            new XAttribute("name", "id"), GroupId
                        ));
                        rootElement.Add(new XElement("field",
                             new XAttribute("name", "ServerName"), ""
                        ));
                        rootElement.Add(new XElement("field",
                             new XAttribute("name", "ServerIP"), ""
                       ));
                        rootElement.Add(new XElement("field",
                             new XAttribute("name", "ServerPort"), 0
                       ));
                        rootElement.Add(new XElement("field",
                             new XAttribute("name", "FlashName"), group.GroupName
                       ));
                        rootElement.Add(new XElement("field",
                             new XAttribute("name", "FlashIcon"), group.GroupIcon
                       ));
                        rootElement.Add(new XElement("field",
                             new XAttribute("name", "FlashHint")
                       ));
                        rootElement.Add(new XElement("field",
                             new XAttribute("name", "Child"), group.Servers.Count
                       ));
                        rootElement.Add(new XElement("field",
                             new XAttribute("name", "PicServerIP"), 0
                       ));
                        rootElement.Add(new XElement("field",
                             new XAttribute("name", "PicServerPort"), 0
                       ));
                        rootElement.Add(new XElement("field",
                             new XAttribute("name", "BindServerIP"), 0
                       ));
                        rootElement.Add(new XElement("field",
                             new XAttribute("name", "BindServerPort"), 0
                       ));
                        rootElement.Add(new XElement("field",
                             new XAttribute("name", "Charges"), 0
                       ));
                        groupElements.Add(rootElement);
                    }
                    catch (Exception e)
                    {
                        System.Windows.Forms.MessageBox.Show(e.Message);
                    }
                    GroupId++;
                    foreach (ServerConfiguration serv in group.Servers)
                    {
                        if (Config.DefaultServer.ServerName == serv.ServerName && Config.DefaultServer.ServerVersion == serv.ServerVersion) // Its the selected server
                        {
                            uint GroupIndex = GroupId - 2;
                            uint ServerIndex = (uint)initialId - 1;
                            SetSelectedServer(GroupIndex, ServerIndex);
                        }
                        XElement rowElement = new XElement("row");
                        try
                        {
                            rowElement.Add(new XElement("field",
                                new XAttribute("name", "id"), (GroupId - 1) + (initialId.ToString().PadLeft(2, '0'))
                            )); ;
                            rowElement.Add(new XElement("field",
                                 new XAttribute("name", "ServerName"), serv.ServerName
                            ));
                            rowElement.Add(new XElement("field",
                                 new XAttribute("name", "ServerIP"), ResolveToIPv4(serv.LoginHost)
                           ));
                            rowElement.Add(new XElement("field",
                                 new XAttribute("name", "ServerPort"), serv.LoginPort
                           ));
                            rowElement.Add(new XElement("field",
                                 new XAttribute("name", "FlashName"), serv.ServerName
                           ));
                            rowElement.Add(new XElement("field",
                                 new XAttribute("name", "FlashIcon"), serv.ServerIcon
                           ));
                            rowElement.Add(new XElement("field",
                                 new XAttribute("name", "FlashHint")
                           ));
                            rowElement.Add(new XElement("field",
                                 new XAttribute("name", "Child"), 0
                           ));
                            rowElement.Add(new XElement("field",
                                 new XAttribute("name", "PicServerIP"), null
                           ));
                            rowElement.Add(new XElement("field",
                                 new XAttribute("name", "PicServerPort"), 0
                           ));
                            rowElement.Add(new XElement("field",
                                 new XAttribute("name", "BindServerIP"), null
                           ));
                            rowElement.Add(new XElement("field",
                                 new XAttribute("name", "BindServerPort"), 0
                           ));
                            rowElement.Add(new XElement("field",
                                 new XAttribute("name", "Charges"), 0
                           ));
                            serverElements.Add(rowElement);
                        }
                        catch (Exception e)
                        {
                            System.Windows.Forms.MessageBox.Show(e.Message);
                        }
                        initialId++;
                    }
                    initialId = 1;
                }
                foreach (XElement xElGroup in groupElements)
                {
                    tableRowsBase.Add(xElGroup);
                }
                foreach (XElement xElServ in serverElements)
                {
                    tableRowsBase.Add(xElServ);
                }
                MemoryStream memStream = new MemoryStream();
                XmlWriterSettings xws = new XmlWriterSettings { OmitXmlDeclaration = false, Indent = true, Encoding = new UTF8Encoding(false) };
                XmlWriter writer = XmlWriter.Create(memStream, xws);
                doc.Save(writer);
                writer.Close();
                memStream.Position = 0;
                if (Config.DefaultServer.ServerVersion >= Constants.MinVersionUseRAWServerDat && Config.DefaultServer.ServerVersion <= Constants.MaxVersionUseRAWServerDat)
                {
                    File.WriteAllBytes("COServer.dat", memStream.ToArray());
                } else
                {
                    using (FileStream compressedFileStream = File.Create("Servers.dat"))
                    {
                        using (GZipStream compressionStream = new GZipStream(compressedFileStream, CompressionMode.Compress))
                        {
                            memStream.CopyTo(compressionStream);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Core.LogWritter.Write($"Error generating Servers.dat: {e}");
            }
        }

        /// <summary>
        /// Resolves a hostname to a dotted-quad IPv4 address for the ServerIP field.
        ///
        /// The client will not do this itself. It validates ServerIP as a numeric
        /// address and rejects anything else outright - its own log says
        /// "ERROR: IP addr too long at ...\3drole\network\socket.h, 130" - without
        /// ever attempting a DNS lookup. So a hostname has to be resolved here,
        /// before it is written into server.dat.
        ///
        /// Note this is not covered by ENABLE_HOSTNAME/HOSTNAME in CLHook.ini: that
        /// path lives in CLHook.dll, and clients from 6000 up are launched with
        /// COHook.dll and the server.dat mechanism instead, so those keys are read
        /// by nobody for a modern client.
        ///
        /// Addresses that are already numeric pass straight through, so existing
        /// configurations behave exactly as before and cost no lookup.
        /// </summary>
        private static string ResolveToIPv4(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return host;

            if (IPAddress.TryParse(host, out IPAddress literal))
                return literal.ToString();

            try
            {
                foreach (IPAddress address in Dns.GetHostAddresses(host))
                {
                    // IPv4 only - the client's parser accepts nothing else.
                    if (address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        Core.LogWritter.Write($"Resolved ServerIP '{host}' to {address}.");
                        return address.ToString();
                    }
                }
                Core.LogWritter.Write($"'{host}' resolved but has no IPv4 address; passing it through unresolved.");
            }
            catch (Exception e)
            {
                // Deliberately not fatal: pass the original string through so the
                // behaviour is exactly what it was before this method existed, and
                // the client reports the failure as it always did. Swallowing this
                // into a launch-blocking error would break configurations that use
                // a literal address and never needed DNS at all.
                Core.LogWritter.Write($"Could not resolve ServerIP '{host}': {e.Message}. Passing it through unresolved.");
            }

            return host;
        }

        public void SetSelectedServer(uint GroupIndex, uint ServerIndex)
        {
            string SetupIniPath = Path.Combine(Directory.GetCurrentDirectory(), "ini", "GameSetup.ini");
            IniManager parser = new IniManager(SetupIniPath, "ScreenMode");
            parser.Write("Group", "GroupRecord", GroupIndex);
            parser.Write("Server", "ServerRecord", ServerIndex);
        }
    }
}
