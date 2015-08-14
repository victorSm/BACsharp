﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Windows.Forms;
using BACnet;

namespace BACnet
{
    //-----------------------------------------------------------------------------------------------
    // The Stack
    public class BACnetStack : IBACnetStack
    {

        public const int BACNET_UNICAST_REQUEST_REPEAT_COUNT = 3; // repeat request x times
        public const int BACNET_UNICAST_REQUEST_REPEAT_TIME = 400; // time between two repeatitions
        public const int BACNET_BROADCAST_REQUEST_REPEAT_COUNT = 3; // repeat request x times

        //// Start up WSA, open a Socket, and Bind it
        //[DllImport("WinSockWrap.dll")]
        //static extern int WinSockStartUp();

        //// Send a packet to the ip specified (or broadcast)
        //[DllImport("WinSockWrap.dll")]
        //static extern int WinSockSendTo(byte[] bytes, int count, ulong ipaddr);

        //// See if a receive packet is ready
        //[DllImport("WinSockWrap.dll")]
        //static extern int WinSockRecvReady();

        //// Get the receive packet
        //[DllImport("WinSockWrap.dll")]
        //static extern int WinSockRecvFrom(byte[] bytes, ref int count, ref ulong ipaddr);

        //// Shut down the socket
        //[DllImport("WinSockWrap.dll")]
        //static extern int WinSockShutDown();

        //// If there was an error from any of the above, call this method
        //[DllImport("WinSockWrap.dll")]
        //static extern int WinSockLastError();

        // Requesting User Confirmed service primitives:
        BACnetServiceRequest Request;
        BACnetServiceConfirm Confirm;

        // Responding User Confirmed service primitives:
        BACnetServiceIndication Indication;
        BACnetServiceResponse Response;

        UdpClient SendUDP = null; // = new Udp  Client(UDPPort);
        UdpClient ReceiveUDP = null; // = new UdpClient(UDPPort, AddressFamily.InterNetwork);

        IPEndPoint LocalEP = null;
        IPEndPoint BroadcastEP = null;
        //IPEndPoint RemoteEP = null;

        private const int UDPPort = 47808;
        private bool TimerDone = false;
        private int InvokeCounter = 0;
        //private byte InvokeCounter = 0;

        // We won't be doing Segments for now
        bool Segmented = false;

        // Create a TSM when a Request is initiated
        TransactionStateMachine TSM;

        // Constructor --------------------------------------------------------------------------------
        //public BACnetStack(string server)
        public BACnetStack()
        {
            // Machine dependent (little endian vs big endian) 
            // In this case we have to reverse the bytes for the Server IP
            byte[] maskbytes = new byte[4];
            byte[] addrbytes = new byte[4];

            //byte[] addr = IPAddress.Parse(server).GetAddressBytes();
            //if (BitConverter.IsLittleEndian) 
            //  Array.Reverse(addr);
            //Server = BitConverter.ToUInt32(addr, 0);

            //if (WinSockStartUp() < 1)
            //  MessageBox.Show("Socket StartUp Error " + WinSockLastError().ToString());

            // Find the local IP address and Subnet Mask
            NetworkInterface[] Interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (NetworkInterface Interface in Interfaces)
            {
                if (Interface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                //MessageBox.Show(Interface.Description);
                UnicastIPAddressInformationCollection UnicastIPInfoCol = Interface.GetIPProperties().UnicastAddresses;
                foreach (UnicastIPAddressInformation UnicatIPInfo in UnicastIPInfoCol)
                {
                    //MessageBox.Show("\tIP Address is {0}" + UnicatIPInfo.Address);
                    //MessageBox.Show("\tSubnet Mask is {0}" + UnicatIPInfo.IPv4Mask);
                    if (UnicatIPInfo.IPv4Mask != null)
                    {
                        byte[] tempbytes = UnicatIPInfo.IPv4Mask.GetAddressBytes();
                        if (tempbytes[0] == 255)
                        {
                            // We found the correct subnet mask, and probably the correct IP address
                            addrbytes = UnicatIPInfo.Address.GetAddressBytes();
                            maskbytes = UnicatIPInfo.IPv4Mask.GetAddressBytes();
                            break;
                        }
                    }
                }
            }
            // Set up broadcast address
            if (maskbytes[3] == 0) maskbytes[3] = 255; else maskbytes[3] = addrbytes[3];
            if (maskbytes[2] == 0) maskbytes[2] = 255; else maskbytes[2] = addrbytes[2];
            if (maskbytes[1] == 0) maskbytes[1] = 255; else maskbytes[1] = addrbytes[1];
            if (maskbytes[0] == 0) maskbytes[0] = 255; else maskbytes[0] = addrbytes[0];
            IPAddress myip = new IPAddress(addrbytes);
            IPAddress broadcast = new IPAddress(maskbytes);

            LocalEP = new IPEndPoint(myip, UDPPort);
            BroadcastEP = new IPEndPoint(broadcast, UDPPort);

            SendUDP = new UdpClient();
            SendUDP.ExclusiveAddressUse = false;
            SendUDP.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            SendUDP.Client.Bind(LocalEP);

            ReceiveUDP = new UdpClient(UDPPort, AddressFamily.InterNetwork);

            //// Create a TSM
            //TSM = new TransactionStateMachine();

            // Init the Devices list
            BACnetData.Devices = new List<Device>();
        }

        // Bind Device Instance to the BACnet Address (we need SNET, SLEN, SADR, etc)
        public bool /*BACnetStack*/ BindBACnetDevice(UInt32 instance, ref int devidx)
        {
            // Linear (brute force) search for now
            for (int i = 0; i < BACnetData.Devices.Count; i++)
            {
                Device dev = BACnetData.Devices[i];
                if (instance == dev.Instance)
                {
                    devidx = i;
                    return true;
                }
            }
            return false;
        }

        // Timer Event for the Socket I/O
        private void /*BACnetStack*/ Timer_Tick(object sender, EventArgs e)
        {
            TimerDone = true;
        }

        public bool /*BACnetStack*/ GetIAm(int network, UInt32 objectid)
        {
            // Wait for I-Am packet
            Byte[] recvBytes = new Byte[512];
            bool found = false;

            // Create the timer
            Timer IAmTimer = new Timer();
            using (IAmTimer)
            {
                IAmTimer.Tick += new EventHandler(Timer_Tick);

                try
                {
                    Socket sock = ReceiveUDP.Client;
                    IPEndPoint RemoteIpEndPoint = new IPEndPoint(IPAddress.Any, 0);

                    // Start the timer
                    TimerDone = false;
                    IAmTimer.Interval = 1000;
                    IAmTimer.Start();
                    while (!TimerDone && !found)
                    {
                        Application.DoEvents();

                        // Process receive packets
                        if (sock.Available > 0)
                        {
                            recvBytes = ReceiveUDP.Receive(ref RemoteIpEndPoint);
                            {
                                // Parse the packet - is it IAm?
                                int NPDUOffset = BVLC.Parse(recvBytes, 0);
                                int APDUOffset = NPDU.Parse(recvBytes, NPDUOffset);
                                if (APDU.ParseIAm(recvBytes, APDUOffset) > 0)
                                {
                                    if ((network == NPDU.SNET) && (objectid == APDU.ObjectID))
                                    {
                                        // Found it!
                                        found = true;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    MessageBox.Show(e.Message);
                }
                finally
                {
                    IAmTimer.Stop();
                }
            }
            return found;
        }

        // Do a Who-Is, and collect information about who answers -------------------------------------
        public List<Device>  /*BACnetStack*/ GetDevices(int milliseconds)
        {
            // Get the host data, send a Who-Is, accept responses and save in the DeviceList
            //ulong ipaddr = 0;
            //int count = 0;
            Byte[] sendBytes = new Byte[12];
            Byte[] recvBytes = new Byte[512];

            // Dns stuff obsoleted ...
            //string hostname = Dns.GetHostName();
            //IPHostEntry host = Dns.GetHostByName(hostname);
            //IPHostEntry host = Dns.GetHostEntry(hostname);

            BACnetData.Devices.Clear();

            // Send the request
            //MessageBox.Show("Send Who-Is (" + broadcast + ")");
            //MessageBox.Show("Send Who-Is");

            // Create the timer
            Timer IAmTimer = new Timer();
            using (IAmTimer)
            {
                IAmTimer.Tick += new EventHandler(Timer_Tick);

                try
                {
                    //PEP Use NPDU.Create and APDU.Create (when written)
                    sendBytes[0] = BVLC.BACNET_BVLC_TYPE_BIP;
                    sendBytes[1] = BVLC.BACNET_BVLC_FUNC_UNICAST_NPDU;
                    sendBytes[2] = 0;
                    sendBytes[3] = 12;
                    sendBytes[4] = BACnetEnums.BACNET_PROTOCOL_VERSION;
                    sendBytes[5] = 0x20;  // Control flags
                    sendBytes[6] = 0xFF;  // Destination network address (65535)
                    sendBytes[7] = 0xFF;
                    sendBytes[8] = 0;     // Destination MAC layer address length, 0 = Broadcast
                    sendBytes[9] = 0xFF;  // Hop count = 255

                    sendBytes[10] = (Byte)BACnetEnums.BACNET_PDU_TYPE.PDU_TYPE_UNCONFIRMED_SERVICE_REQUEST;
                    sendBytes[11] = (Byte)BACnetEnums.BACNET_UNCONFIRMED_SERVICE.SERVICE_UNCONFIRMED_WHO_IS;

                    //ipaddr = 0xC0A85CFF; // 192.168.92.FF
                    //if (WinSockSendTo(sendBytes, 12, ipaddr) < 1)
                    //{
                    //  MessageBox.Show("Socket Send Error " + WinSockLastError().ToString());
                    //  return;
                    //}
                    // Send the broadcast "who-is"
                    //SendUDP.EnableBroadcast = true;
                    //SendUDP.Connect(broadcast, UDPPort);
                    SendUDP.EnableBroadcast = true;
                    SendUDP.Send(sendBytes, 12, BroadcastEP);

                    Socket sock = ReceiveUDP.Client;
                    IPEndPoint RemoteIpEndPoint = new IPEndPoint(IPAddress.Any, 0);

                    // Start the timer so we can receive multiple responses
                    TimerDone = false;
                    IAmTimer.Interval = milliseconds;
                    IAmTimer.Start();
                    while (!TimerDone)
                    {
                        Application.DoEvents();

                        // Process the response packets
                        //if (WinSockRecvReady() > 0)
                        //{
                        //  if (WinSockRecvFrom(recvBytes, ref count, ref ipaddr) > 0)
                        // Process the response packets
                        if (SendUDP.Client.Available > 0)
                        {
                            recvBytes = SendUDP.Receive(ref RemoteIpEndPoint);
                            {
                                // Parse and save the BACnet data
                                int NPDUOffset = BVLC.Parse(recvBytes, 0); 
                                int APDUOffset = NPDU.Parse(recvBytes, NPDUOffset);
                                if (APDU.ParseIAm(recvBytes, APDUOffset) > 0)
                                {
                                    Device device       = new Device();
                                    device.Name         = "Device";
                                    device.SourceLength = NPDU.SLEN;
                                    device.ServerEP     = RemoteIpEndPoint;
                                    device.Network      = NPDU.SNET;
                                    device.MACAddress   = NPDU.SAddress;
                                    device.Instance     = APDU.ObjectID;
                                    if (!BACnetData.Devices.Contains(device))
                                    {
                                        BACnetData.Devices.Add(device);
                                    }

                                    // We should now have enough info to read/write properties for this device
                                }
                            }
                            // Restart the timer - as long as I-AM packets come, we'll wait
                            IAmTimer.Stop();
                            IAmTimer.Start();
                        }
                    }
                    return BACnetData.Devices;
                }
                catch (Exception e)
                {
                    MessageBox.Show(e.StackTrace);
                }
                finally
                {
                    IAmTimer.Stop();
                }
            }
            return BACnetData.Devices;
        }

        // Read Property -------------------------------------------------------------------------
        public bool /*BACnetStack*/ SendReadProperty(
          Device recipient,
          int arrayidx,
          BACnetEnums.BACNET_OBJECT_TYPE objtype,
          BACnetEnums.BACNET_PROPERTY_ID objprop,
          Property property)

          //out string value)
        // Parameters:
        //   Device index (for network and MAC address),
        //   Object Type, 
        //   Property ID,
        //   Value returned
        {
            // Create and send an Confirmed Request

            //value = "(none)";
            if (recipient == null) return false;

            if (property == null) return false;

            //uint instance = BACnetData.Devices[deviceidx].Instance;

            Byte[] sendBytes = new Byte[50];
            Byte[] recvBytes = new Byte[512];
            uint len;

            // BVLL
            sendBytes[0] = BVLC.BACNET_BVLC_TYPE_BIP;
            sendBytes[1] = BVLC.BACNET_BVLC_FUNC_UNICAST_NPDU;
            sendBytes[2] = 0x00;
            sendBytes[3] = 0x00;  // BVLL Length, fix later (24?)

            // NPDU
            sendBytes[4] = BACnetEnums.BACNET_PROTOCOL_VERSION;
            if (recipient.SourceLength == 0)
                sendBytes[5] = 0x04;  // Control flags, no destination address
            else
                sendBytes[5] = 0x24;  // Control flags, with broadcast or destination address

            len = 6;
            if (recipient.SourceLength > 0)
            {
                // Get the (MSTP) Network number (2001)
                //sendBytes[6] = 0x07;  // Destination network address (2001)
                //sendBytes[7] = 0xD1;
                byte[] temp2 = new byte[2];
                temp2 = BitConverter.GetBytes(recipient.Network);
                sendBytes[len++] = temp2[1];
                sendBytes[len++] = temp2[0];

                // Get the MAC address (0x0D)
                //sendBytes[8] = 0x01;  // MAC address length
                //sendBytes[9] = 0x0D;  // Destination MAC layer address
                byte[] temp4 = new byte[4];
                temp4 = BitConverter.GetBytes(recipient.MACAddress);

                sendBytes[len++] = 0x01;  // MAC address length - adjust for other lengths ...
                sendBytes[len++] = temp4[0];
                sendBytes[len++] = 0xFF;  // Hop count = 255
            }

            // APDU
            sendBytes[len++] = 0x00;  // Control flags
            sendBytes[len++] = 0x05;  // Max APDU length (1476)

            // Create invoke counter
            sendBytes[len++] = (byte)(InvokeCounter);
            InvokeCounter = ((InvokeCounter + 1) & 0xFF);

            sendBytes[len++] = 0x0C;  // Service Choice: Read Property request

            // Service Request (var part of APDU):
            // Set up Object ID (Context Tag)
            len = APDU.SetObjectID(ref sendBytes, len, objtype, recipient.Instance);

            // Set up Property ID (Context Tag)
            len = APDU.SetPropertyID(ref sendBytes, len, objprop);

            // Optional array index goes here
            if (arrayidx >= 0)
                len = APDU.SetArrayIdx(ref sendBytes, len, arrayidx);

            // Fix the BVLL length
            sendBytes[3] = (byte)len;

            // Create the timer (we could use a blocking recvFrom instead ...)
            Timer ReadPropTimer = new Timer();
            try
            {
                int Count = 0;
                using (ReadPropTimer)
                {
                    ReadPropTimer.Tick += new EventHandler(Timer_Tick);

                    while (Count < 3)
                    {
                        SendUDP.EnableBroadcast = false;
                        SendUDP.Send(sendBytes, (int)len, recipient.ServerEP);

                        // Start the timer
                        TimerDone = false;
                        ReadPropTimer.Interval = 400;  // 100 ms
                        ReadPropTimer.Start();
                        while (!TimerDone)
                        {
                            // Wait for Confirmed Response
                            Application.DoEvents();

                            if (SendUDP.Client.Available > 0)
                            {
                                //recvBytes = SendUDP.Receive(ref RemoteEP);
                                IPEndPoint sendTo = recipient.ServerEP;
                                recvBytes = SendUDP.Receive(ref sendTo);

                                int APDUOffset = NPDU.Parse(recvBytes, BVLC.BACNET_BVLC_HEADER_LEN); // BVLL is always 4 bytes

                                // Check for APDU response 
                                // 0x - Confirmed Request 
                                // 1x - Un-Confirmed Request
                                // 2x - Simple ACK
                                // 3x - Complex ACK
                                // 4x - Segment ACK
                                // 5x - Error
                                // 6x - Reject
                                // 7x - Abort
                                if (recvBytes[APDUOffset] == 0x30)
                                {
                                    // Verify the Invoke ID is the same
                                    byte ic = (byte)(InvokeCounter == 0 ? 255 : InvokeCounter - 1);
                                    if (ic == recvBytes[APDUOffset + 1])
                                    {
                                        APDU.ParseProperty(ref recvBytes, APDUOffset, property);
                                        return true;  // This will still execute the finally
                                    }
                                    //else
                                    //{
                                    //  MessageBox.Show("Invoke Counter Error");
                                    //  return false;
                                    //}
                                }
                            }
                        }
                        Count++;
                        BACnetData.PacketRetryCount++;
                        ReadPropTimer.Stop(); // We'll start it over at the top of the loop
                    }
                    return false;  // This will still execute the finally
                }
            }
            finally
            {
                ReadPropTimer.Stop();
            }
        }

        // Write Property -------------------------------------------------------------------------
        public bool /*BACnetStack*/ SendWriteProperty(
          Device recipient,
          int arrayidx,
          BACnetEnums.BACNET_OBJECT_TYPE objtype,
          BACnetEnums.BACNET_PROPERTY_ID objprop,
          Property property,
          int priority)
        // Parameters:
        //   Device index (for network and MAC address),
        //   Object Type, 
        //   Property ID,
        //   Property Value
        //   Priority
        {
            // Create and send an Confirmed Request
            if (recipient == null) return false;

            if (property == null) return false;

            Byte[] sendBytes = new Byte[50];
            Byte[] recvBytes = new Byte[512];
            
            // BVLL
            uint len = BVLC.Fill(ref sendBytes, BVLC.BACNET_BVLC_FUNC_UNICAST_NPDU, 0);

            // NPDU
            sendBytes[len++] = BACnetEnums.BACNET_PROTOCOL_VERSION;
            if (recipient.SourceLength == 0)
                sendBytes[len++] = 0x04;  // Control flags, no destination address
            else
                sendBytes[len++] = 0x24;  // Control flags, with broadcast or destination

            if (recipient.SourceLength > 0)
            {
                // Get the (MSTP) Network number (2001)
                //sendBytes[6] = 0x07;  // Destination network address (2001)
                //sendBytes[7] = 0xD1;
                byte[] temp2 = new byte[2];
                temp2 = BitConverter.GetBytes(recipient.Network);
                sendBytes[len++] = temp2[1];
                sendBytes[len++] = temp2[0];

                // Get the MAC address (0x0D)
                //sendBytes[8] = 0x01;  // MAC address length
                //sendBytes[9] = 0x0D;  // Destination MAC layer address
                byte[] temp4 = new byte[4];
                temp4 = BitConverter.GetBytes(recipient.MACAddress);
                sendBytes[len++] = 0x01;  // MAC address length - adjust for other lengths ...
                sendBytes[len++] = temp4[0];

                sendBytes[len++] = 0xFF;  // Hop count = 255
            }

            // APDU
            sendBytes[len++] = 0x00;  // Control flags
            sendBytes[len++] = 0x05;  // Max APDU length (1476)

            // Create invoke counter
            //sendBytes[len++] = InvokeCounter++;  // Invoke ID
            sendBytes[len++] = (byte)(InvokeCounter);
            InvokeCounter = ((InvokeCounter + 1) & 0xFF);

            sendBytes[len++] = 0x0F;  // Service Choice: Write Property request

            // Service Request (var part of APDU):
            // Set up Object ID (Context Tag)
            len = APDU.SetObjectID(ref sendBytes, len, objtype, recipient.Instance);

            // Set up Property ID (Context Tag)
            len = APDU.SetPropertyID(ref sendBytes, len, objprop);

            // Optional array index goes here
            if (arrayidx >= 0)
                len = APDU.SetArrayIdx(ref sendBytes, len, arrayidx);

            // Set the value to send
            len = APDU.SetProperty(ref sendBytes, len, property);

            //PEP Optional array index goes here

            // Set priority
            if (priority > 0)
                len = APDU.SetPriority(ref sendBytes, len, priority);

            // Fix the BVLL length
            sendBytes[3] = (byte)len;

            // Create the timer (we could use a blocking recvFrom instead ...)
            Timer ReadPropTimer = new Timer();

            try
            {
                using (ReadPropTimer)
                {
                    int Count = 0;
                    ReadPropTimer.Tick += new EventHandler(Timer_Tick);

                    while (Count < BACNET_UNICAST_REQUEST_REPEAT_COUNT)
                    {
                        SendUDP.EnableBroadcast = false;
                        SendUDP.Send(sendBytes, (int)len, recipient.ServerEP);

                        // Start the timer
                        TimerDone = false;
                        ReadPropTimer.Interval = BACNET_UNICAST_REQUEST_REPEAT_TIME;
                        ReadPropTimer.Start();
                        while (!TimerDone)
                        {
                            // Wait for Confirmed Response
                            Application.DoEvents();

                            if (SendUDP.Client.Available > 0)
                            {
                                //recvBytes = SendUDP.Receive(ref RemoteEP);
                                IPEndPoint sendTo = recipient.ServerEP;
                                recvBytes = SendUDP.Receive(ref sendTo);

                                int APDUOffset = NPDU.Parse(recvBytes, 4); // BVLL is always 4 bytes
                                // Check for APDU response, and decide what to do
                                // 0x - Confirmed Request 
                                // 1x - Un-Confirmed Request
                                // 2x - Simple ACK
                                // 3x - Complex ACK
                                // 4x - Segment ACK
                                // 5x - Error
                                // 6x - Reject
                                // 7x - Abort
                                if (recvBytes[APDUOffset] == 0x20)
                                {
                                    // Verify the Invoke ID is the same
                                    byte ic = (byte)(InvokeCounter == 0 ? 255 : InvokeCounter - 1);
                                    if (ic == recvBytes[APDUOffset + 1])
                                    {
                                        return true; // This will still execute the finally
                                    }
                                    //else
                                    //{
                                    //  MessageBox.Show("Invoke Counter Error");
                                    //  return false;
                                    //}
                                }
                            }
                        }
                        Count++;
                        BACnetData.PacketRetryCount++;
                        ReadPropTimer.Stop(); // We'll start it over at the top of the loop
                    }
                    return false; // This will still execute the finally
                }
            }
            finally
            {
                ReadPropTimer.Stop();
            }
        }

        // Read Broadcast Distribution Table -------------------------------------------------------------------------
        public bool /*BACnetStack*/ SendReadBdt(IPEndPoint remoteEP)
        //out string value)
        // Parameters:
        //   Device index (for network and MAC address),
        //   Value returned
        {
            // Create and send an Confirmed Request
            if (remoteEP == null) return false;

            //uint instance = BACnetData.Devices[deviceidx].Instance;

            Byte[] sendBytes = new Byte[50];
            Byte[] recvBytes = new Byte[512];
            
            // BVLL
            uint len = BVLC.Fill(ref sendBytes, BVLC.BACNET_BVLC_FUNC_READ_BDT, 0);

            // Create the timer (we could use a blocking recvFrom instead ...)
            Timer BVLCFuncTimer = new Timer();
            try
            {
                int Count = 0;
                using (BVLCFuncTimer)
                {
                    BVLCFuncTimer.Tick += new EventHandler(Timer_Tick);

                    bool gotResponse = false;

                    while (Count < BACNET_UNICAST_REQUEST_REPEAT_COUNT && !gotResponse)
                    {
                        SendUDP.EnableBroadcast = false;
                        SendUDP.Send(sendBytes, (int)len, remoteEP);

                        // Start the timer
                        TimerDone = false;
                        BVLCFuncTimer.Interval = BACNET_UNICAST_REQUEST_REPEAT_TIME;  // 400 ms
                        BVLCFuncTimer.Start();
                        while (!TimerDone && !gotResponse)
                        {
                            // Wait for Confirmed Response
                            Application.DoEvents();

                            if (SendUDP.Client.Available > 0)
                            {
                                recvBytes = SendUDP.Receive(ref remoteEP);
                                Console.WriteLine("Received " + recvBytes.Length + " bytes as response.");
                                for (int i = 0; i < recvBytes.Length; i++)
                                {
                                    Console.Write(recvBytes[i].ToString("x")+",");
                                }
                                Console.WriteLine();

                                BVLC.Parse(recvBytes, 0);
                                if (BVLC.BACNET_BVLC_FUNC_READ_BDT_ACK == BVLC.BVLC_Function &&
                                    null != BVLC.BVLC_ListOfBdtEntries)
                                {
                                    for (int i = 0; i < BVLC.BVLC_ListOfBdtEntries.Length; i++)
                                    {
                                        Console.WriteLine("BBMD: IP " +
                                                          BVLC.BVLC_ListOfBdtEntries[i].MACAddress.Address.ToString() +
                                                          ":" +
                                                          BVLC.BVLC_ListOfBdtEntries[i].MACAddress.Port.ToString() +
                                                          " Mask " +
                                                          BVLC.BVLC_ListOfBdtEntries[i].Mask.ToString());
                                    }
                                    gotResponse = true; ;
                                }
                                else if (BVLC.BACNET_BVLC_FUNC_RESULT == BVLC.BVLC_Function)
                                {
                                    if (0x0020 == BVLC.BVLC_Function_ResultCode)
                                    {
                                        gotResponse = true;
                                    }
                                }
                                else
                                {
                                    Console.WriteLine("Received BVLC Paket Type " + BVLC.BVLC_Function.ToString() + " expected:  " + BVLC.BACNET_BVLC_FUNC_READ_BDT_ACK);
                                }


                            }
                        }
                        Count++;
                        BACnetData.PacketRetryCount++;
                        BVLCFuncTimer.Stop(); // We'll start it over at the top of the loop
                    }
                    return gotResponse;  // This will still execute the finally
                }
            }
            finally
            {
                BVLCFuncTimer.Stop();
            }
        }

        // Read Foreign Device Table -------------------------------------------------------------------------
        public bool /*BACnetStack*/ SendReadFdt(IPEndPoint remoteEP)
        //out string value)
        // Parameters:
        //   Device index (for network and MAC address),
        //   Value returned
        {
            // Create and send an Confirmed Request
            if (remoteEP == null) return false;


            Byte[] sendBytes = new Byte[50];
            Byte[] recvBytes = new Byte[512];

            // BVLL
            uint len = BVLC.Fill(ref sendBytes, BVLC.BACNET_BVLC_FUNC_READ_FDT, 0);

            // Create the timer (we could use a blocking recvFrom instead ...)
            Timer BVLCFuncTimer = new Timer();
            try
            {
                int Count = 0;
                using (BVLCFuncTimer)
                {
                    BVLCFuncTimer.Tick += new EventHandler(Timer_Tick);
                    
                    bool gotResponse = false;

                    while (Count < BACNET_UNICAST_REQUEST_REPEAT_COUNT && !gotResponse)
                    {
                        SendUDP.EnableBroadcast = false;
                        SendUDP.Send(sendBytes, (int)len, remoteEP);

                        // Start the timer
                        TimerDone = false;
                        BVLCFuncTimer.Interval = BACNET_UNICAST_REQUEST_REPEAT_TIME;  // 400 ms
                        BVLCFuncTimer.Start();
                        while (!TimerDone && !gotResponse)
                        {
                            // Wait for Confirmed Response
                            Application.DoEvents();

                            if (SendUDP.Client.Available > 0)
                            {
                                recvBytes = SendUDP.Receive(ref remoteEP);
                                Console.WriteLine("Received " + recvBytes.Length + " bytes as response.");
                                for (int i = 0; i < recvBytes.Length; i++)
                                {
                                    Console.Write(recvBytes[i].ToString("x") + ",");
                                }
                                Console.WriteLine();

                                BVLC.Parse(recvBytes, 0);
                                if (BVLC.BACNET_BVLC_FUNC_READ_FDT_ACK == BVLC.BVLC_Function &&
                                    null != BVLC.BVLC_ListOfFdtEntries)
                                {
                                    for (int i = 0; i < BVLC.BVLC_ListOfFdtEntries.Length; i++)
                                    {
                                        Console.WriteLine("FD: IP " +   BVLC.BVLC_ListOfFdtEntries[i].MACAddress.Address.ToString() + ":" +
                                                                        BVLC.BVLC_ListOfFdtEntries[i].MACAddress.Port.ToString() + " TimeToLive " +
                                                                        BVLC.BVLC_ListOfFdtEntries[i].TimeToLive.ToString() + " TimeRemaining " +
                                                                        BVLC.BVLC_ListOfFdtEntries[i].TimeRemaining.ToString() );
                                    }
                                    gotResponse = true;
                                }
                                else if (BVLC.BACNET_BVLC_FUNC_RESULT == BVLC.BVLC_Function)
                                {
                                    if (0x0040 == BVLC.BVLC_Function_ResultCode)
                                    {
                                        gotResponse = true;
                                    }
                                }
                                else
                                {
                                    Console.WriteLine("Received BVLC Paket Type " + BVLC.BVLC_Function.ToString() +
                                                      " expected:  " + BVLC.BACNET_BVLC_FUNC_READ_FDT_ACK);
                                }

                            }
                        }
                        Count++;
                        BACnetData.PacketRetryCount++;
                        BVLCFuncTimer.Stop(); // We'll start it over at the top of the loop
                    }
                    return gotResponse;  // This will still execute the finally
                }
            }
            finally
            {
                BVLCFuncTimer.Stop();
            }
        }
    }



}
