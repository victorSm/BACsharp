﻿using System;
using System.Collections.Generic;
using System.Net;

namespace BACnet
{
    //-----------------------------------------------------------------------------------------------
    // The BACnetManager
    public class BACnetManager : IBACnetManager
    {
        private IBACnetStack BACStack = null;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="localIpAddress">The host's IP address where BACnet communication should take place.</param>
        public BACnetManager(IPAddress localIpAddress)
        {
            try
            {
                //IPAddress localIpAddress = System.Net.Dns.GetHostByName(Environment.MachineName).AddressList[0];
                BACStack = new BACnetStack(localIpAddress);
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        /// <summary>
        /// Find all BACnet devices.
        /// </summary>
        /// <returns>
        /// A list of all found BACnet IP devices.
        /// </returns>
        /// <remarks>
        /// BACnet devices should return an I-Am when requested by a Who-Is (with no device instance range).
        /// Use unicast Who-Is service as we're not registered at a BBMD in the network.
        /// Reading the required Device properties can be accomplished with a single ReadPropertyMultiple request.
        /// </remarks>
        public List<BACnetDevice> FindBACnetDevices()
        {
            try
            {
                List<Device> FoundDevices = BACStack.GetDevices(2000);
                List<BACnetDevice> FoundIpBacnetDevices = new List<BACnetDevice>();
                for (UInt16 i = 0; i < FoundDevices.Count; i++)
                {
                    FoundIpBacnetDevices.Add(new BACnetDevice(FoundDevices[i].ServerEP, FoundDevices[i].Network,
                        (uint)FoundDevices[i].Instance, FoundDevices[i].VendorID, FoundDevices[i].SourceLength));
                }
                return FoundIpBacnetDevices;
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        /// <summary>
        /// Finds the device properties.
        /// </summary>
        /// <param name="device"></param>
        /// <returns></returns>
        public bool FindDeviceProperties(ref BACnetDevice device)
        {
            bool successful = true;

            if (!(GetObjectName(ref device))) { successful = false; }
            if (!(GetApplicationSoftwareVersion(ref device))) { successful = false; }
            if (!(GetModelName(ref device))) { successful = false; }
            if (!(GetFirmwareRevision(ref device))) { successful = false; }

            if (!(GetVendorIdentifier(ref device))) { successful = false; }
            if (!(GetProtocolRevision(ref device))) { successful = false; }
            if (!(GetProtocolVersion(ref device))) { successful = false; }

            if (!(GetSystemStatus(ref device))) { successful = false; }

            return successful;
        }

        /// <summary>
        /// Finds the device objects.
        /// </summary>
        /// <param name="device">The device.</param>
        /// <returns></returns>
        public bool FindDeviceObjects(ref BACnetDevice device)
        {
            bool successful = true;

            List<string> deviceObjects = new List<string>();

            Device recipient = new Device();
            recipient.ServerEP = device.IpAddress;
            recipient.Instance = device.InstanceNumber;
            recipient.Network = device.Network;
            recipient.SourceLength = device.SourceLength;

            Property property = new Property();
            property.Tag = BACnetEnums.BACNET_APPLICATION_TAG.BACNET_APPLICATION_TAG_ENUMERATED;
            if (!BACStack.SendReadProperty(
              recipient,
              0, // Array[0] is Object Count
              BACnetEnums.BACNET_OBJECT_TYPE.OBJECT_DEVICE,
              BACnetEnums.BACNET_PROPERTY_ID.PROP_OBJECT_LIST,
              property))
            {
                successful = false;
            }

            if (property.Tag != BACnetEnums.BACNET_APPLICATION_TAG.BACNET_APPLICATION_TAG_UNSIGNED_INT)
            {
                successful = false;
            }

            int i, tries;
            uint total = property.ValueUInt;
            if (total > 0) for (i = 1; i <= total; i++)
                {
                    // Read through Array[x] up to Object Count
                    // Need to try the read again if it times out
                    tries = 0;
                    while (tries < 5)
                    {
                        tries++;
                        if (BACStack.SendReadProperty(
                          recipient,
                          i, // each array index
                          BACnetEnums.BACNET_OBJECT_TYPE.OBJECT_DEVICE,
                          BACnetEnums.BACNET_PROPERTY_ID.PROP_OBJECT_LIST,
                          property))
                        {
                            tries = 5; // Next object
                            string s;
                            if (property.Tag != BACnetEnums.BACNET_APPLICATION_TAG.BACNET_APPLICATION_TAG_OBJECT_ID)
                                tries = 5; // continue;
                            switch (property.ValueObjectType)
                            {
                                case BACnetEnums.BACNET_OBJECT_TYPE.OBJECT_DEVICE: s = "Device"; break;
                                case BACnetEnums.BACNET_OBJECT_TYPE.OBJECT_ANALOG_INPUT: s = "Analog Input"; break;
                                case BACnetEnums.BACNET_OBJECT_TYPE.OBJECT_ANALOG_OUTPUT: s = "Analog Output"; break;
                                case BACnetEnums.BACNET_OBJECT_TYPE.OBJECT_ANALOG_VALUE: s = "Analog value"; break;
                                case BACnetEnums.BACNET_OBJECT_TYPE.OBJECT_BINARY_INPUT: s = "Binary Input"; break;
                                case BACnetEnums.BACNET_OBJECT_TYPE.OBJECT_BINARY_OUTPUT: s = "Binary Output"; break;
                                case BACnetEnums.BACNET_OBJECT_TYPE.OBJECT_BINARY_VALUE: s = "Binary value"; break;
                                case BACnetEnums.BACNET_OBJECT_TYPE.OBJECT_FILE: s = "File"; break;
                                default: s = "Other"; break;
                            }
                            s = s + "  " + property.ValueObjectInstance.ToString();
                            deviceObjects.Add(s);
                        }
                    }
                }
            device.DeviceObjects = deviceObjects;
            return successful;
        }

        /// <summary>
        /// Find all BACnet devices with enabled BBMD functionality.
        /// </summary>
        /// <param name="ipNetwork">The IP network address in CIDR format.</param>
        /// <returns></returns>
        /// <exception cref="System.NotImplementedException"></exception>
        /// <remarks>
        /// Devices with enabled BBMD should ACK ReadBroadcastDistributionTable requests.
        /// BBMDs with enabled FD registration should ACK ReadForeignDeviceTable requests.
        /// </remarks>
        public List<BACnetDeviceWithBBMD> FindBACnetBBMDs(IPAddress ipNetwork)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Determines whether there is a BACnet device at the specified IP address and port.
        /// </summary>
        /// <param name="ipAddress">The ip address.</param>
        /// <returns>
        /// The BACnet Device Instance Number or 4194303 (BACnet "null") if not a BACnet device.
        /// </returns>
        /// <exception cref="System.NotImplementedException"></exception>
        public int GetBACnetDeviceInstanceNumber(IPEndPoint ipAddress)
        {
            Device newDev = (BACStack.UnicastWhoIsOnSingleIp(ipAddress, 1000));
            if (newDev == null)
            {
                return -1;
            }
            else
            {
                return (int)newDev.Instance;
            }
        }

        /// <summary>
        /// Determines whether the BBMD is enabled.
        /// </summary>
        /// <param name="ipAddress">The IP address of a BACnet device.</param>
        /// <returns>
        /// True if BBMD is enabled, false otherwise.
        /// </returns>
        public bool IsBbmdEnabled(IPEndPoint ipAddress)
        {
            if (BACStack.SendReadBdt(ipAddress))
            {
                if (BVLC.BVLC_Function_ResultCode == 0)
                    return true;
                else
                    return false;
            }
            return false;
        }

        /// <summary>
        /// Determines whether the BBMD option "Foreign Device Registration" is enabled on a given BACnet BBMD.
        /// </summary>
        /// <param name="ipAddress">The IP address of a BACnet BBMD.</param>
        /// <returns>
        /// True if FD registration is enabled, false otherwise.
        /// </returns>
        public bool IsFdRegistrationSupported(IPEndPoint ipAddress)
        {
            if (BACStack.SendReadFdt(ipAddress))
            {
                if (BVLC.BVLC_Function_ResultCode == 0)
                    return true;
                else
                    return false;
            }
            return false;
        }

        private bool GetObjectName(ref BACnetDevice device)
        {
            return GetStringPropertyValue(ref device, BACnetEnums.BACNET_PROPERTY_ID.PROP_OBJECT_NAME,
                ref device.ObjectName);
        }
        private bool GetVendorName(ref BACnetDevice device)
        {
            return GetStringPropertyValue(ref device, BACnetEnums.BACNET_PROPERTY_ID.PROP_VENDOR_NAME,
                ref device.VendorName);
        }
        private bool GetModelName(ref BACnetDevice device)
        {
            return GetStringPropertyValue(ref device, BACnetEnums.BACNET_PROPERTY_ID.PROP_MODEL_NAME,
                ref device.ModelName);
        }

        private bool GetApplicationSoftwareVersion(ref BACnetDevice device)
        {
            return GetStringPropertyValue(ref device, BACnetEnums.BACNET_PROPERTY_ID.PROP_APPLICATION_SOFTWARE_VERSION,
                ref device.ApplicationSoftwareVersion);
        }

        private bool GetFirmwareRevision(ref BACnetDevice device)
        {
            return GetStringPropertyValue(ref device, BACnetEnums.BACNET_PROPERTY_ID.PROP_FIRMWARE_REVISION,
                ref device.FirmwareRevision);
        }

        private bool GetVendorIdentifier(ref BACnetDevice device)
        {
            uint value = 0;
            if (GetUnsignedPropertyValue(ref device, BACnetEnums.BACNET_PROPERTY_ID.PROP_VENDOR_IDENTIFIER,
                ref value))
            {
                device.VendorIdentifier = (int)value;
                return true;
            }
            else
            {
                return false;
            }

        }

        private bool GetProtocolVersion(ref BACnetDevice device)
        {
            uint value = 0;
            if (GetUnsignedPropertyValue(ref device, BACnetEnums.BACNET_PROPERTY_ID.PROP_PROTOCOL_VERSION,
                ref value))
            {
                device.ProtocolVersion = (int)value;
                return true;
            }
            else
            {
                return false;
            }

        }

        private bool GetProtocolRevision(ref BACnetDevice device)
        {
            uint value = 0;
            if (GetUnsignedPropertyValue(ref device, BACnetEnums.BACNET_PROPERTY_ID.PROP_PROTOCOL_REVISION,
                ref value))
            {
                device.ProtocolRevision = (int)value;
                return true;
            }
            else
            {
                return false;
            }

        }

        private bool GetSystemStatus(ref BACnetDevice device)
        {
            string[] BacnetDeviceSytemStatusString =
            {
                "operational", "operational-read-only", "download-required",
                "download-in-progress", "non-operational", "backup-in-progress"
            };
            uint value = 0;
            if (GetEnumeratedPropertyValue(ref device, BACnetEnums.BACNET_PROPERTY_ID.PROP_SYSTEM_STATUS,
                ref value))
            {
                if (value < BacnetDeviceSytemStatusString.Length)
                    device.SystemStatus = BacnetDeviceSytemStatusString[value];
                else
                    device.SystemStatus = "SystemStatus " + value.ToString();
                return true;
            }
            else
            {
                device.SystemStatus = "Failed to get System Status " + value.ToString();
                return false;
            }

        }

        private bool GetStringPropertyValue(ref BACnetDevice device, BACnetEnums.BACNET_PROPERTY_ID propId, ref string value)
        {
            Property property = new Property();
            property.Tag = BACnetEnums.BACNET_APPLICATION_TAG.BACNET_APPLICATION_TAG_ENUMERATED;

            Device recipient = new Device();
            recipient.ServerEP = device.IpAddress;
            recipient.Instance = device.InstanceNumber;
            recipient.Network = device.Network;
            recipient.SourceLength = device.SourceLength;

            if (!BACStack.SendReadProperty(
                recipient,
                -1, // Array[0] is Object Count
                BACnetEnums.BACNET_OBJECT_TYPE.OBJECT_DEVICE,
                propId,
                property))
            {
                return false;
            }

            if (property.Tag != BACnetEnums.BACNET_APPLICATION_TAG.BACNET_APPLICATION_TAG_CHARACTER_STRING)
            {
                return false;
            }
            value = property.ValueString;
            return true;
        }

        private bool GetUnsignedPropertyValue(ref BACnetDevice device, BACnetEnums.BACNET_PROPERTY_ID propId, ref uint value)
        {
            Property property = new Property();
            property.Tag = BACnetEnums.BACNET_APPLICATION_TAG.BACNET_APPLICATION_TAG_ENUMERATED;

            Device recipient = new Device();
            recipient.ServerEP = device.IpAddress;
            recipient.Instance = device.InstanceNumber;
            recipient.Network = device.Network;
            recipient.SourceLength = device.SourceLength;

            if (!BACStack.SendReadProperty(
                recipient,
                -1, // Array[0] is Object Count
                BACnetEnums.BACNET_OBJECT_TYPE.OBJECT_DEVICE,
                propId,
                property))
            {
                return false;
            }

            if (property.Tag != BACnetEnums.BACNET_APPLICATION_TAG.BACNET_APPLICATION_TAG_UNSIGNED_INT)
            {
                return false;
            }
            value = property.ValueUInt;
            return true;
        }

        private bool GetEnumeratedPropertyValue(ref BACnetDevice device, BACnetEnums.BACNET_PROPERTY_ID propId, ref uint value)
        {
            Property property = new Property();
            property.Tag = BACnetEnums.BACNET_APPLICATION_TAG.BACNET_APPLICATION_TAG_NULL;

            Device recipient = new Device();
            recipient.ServerEP = device.IpAddress;
            recipient.Instance = device.InstanceNumber;
            recipient.Network = device.Network;
            recipient.SourceLength = device.SourceLength;

            if (!BACStack.SendReadProperty(
                recipient,
                -1, // Array[0] is Object Count
                BACnetEnums.BACNET_OBJECT_TYPE.OBJECT_DEVICE,
                propId,
                property))
            {
                return false;
            }

            if (property.Tag != BACnetEnums.BACNET_APPLICATION_TAG.BACNET_APPLICATION_TAG_ENUMERATED)
            {
                return false;
            }
            value = property.ValueEnum;
            return true;
        }
    }
}




