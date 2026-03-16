using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace EchoLink.Services
{
    public class VirtualMicService
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid ClassGuid, IntPtr hwndParent);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool SetupDiCreateDeviceInfo(IntPtr DeviceInfoSet, string DeviceName, ref Guid ClassGuid, string DeviceDescription, IntPtr hwndParent, uint CreationFlags, out SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool SetupDiSetDeviceRegistryProperty(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, uint Property, byte[] PropertyBuffer, uint PropertyBufferSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiCallClassInstaller(uint InstallFunction, IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("newdev.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool UpdateDriverForPlugAndPlayDevices(
            IntPtr hwndParent,
            string HardwareId,
            string FullInfPath,
            uint InstallFlags,
            out bool bRebootRequired);

        public bool InstallDriver()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string infPath = Path.Combine(baseDir, "VirtualAudioDriver", "VirtualAudioDriver.inf");

            if (!File.Exists(infPath))
            {
                Console.WriteLine($"[ERROR] Driver not found at: {infPath}");
                return false;
            }

            // Step 1: Stage the driver into the Windows Driver Store using pnputil
            var processInfo = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = $"/add-driver \"{infPath}\" /install",
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true
            };

            try
            {
                using var process = Process.Start(processInfo);
                process?.WaitForExit();
                
                // We don't fail here if pnputil fails, as the driver might already be staged 
                // and we still need to instantiate the hardware node.

                // Step 2: Manually instantiate the virtual root hardware node (Replicating devcon install)
                string hwid = "ROOT\\VirtualAudioDriver";
                Guid mediaClass = new Guid("4d36e96c-e325-11ce-bfc1-08002be10318");
                
                IntPtr hdev = SetupDiCreateDeviceInfoList(ref mediaClass, IntPtr.Zero);
                if (hdev != (IntPtr)(-1))
                {
                    SP_DEVINFO_DATA devdata = new SP_DEVINFO_DATA();
                    devdata.cbSize = Marshal.SizeOf(devdata);

                    if (SetupDiCreateDeviceInfo(hdev, "VirtualAudioDriver", ref mediaClass, null!, IntPtr.Zero, 1, out devdata))
                    {
                        byte[] hwidBytes = System.Text.Encoding.Unicode.GetBytes(hwid + "\0\0");
                        if (SetupDiSetDeviceRegistryProperty(hdev, ref devdata, 1 /* SPDRP_HARDWAREID */, hwidBytes, (uint)hwidBytes.Length))
                        {
                            SetupDiCallClassInstaller(0x19 /* DIF_REGISTERDEVICE */, hdev, ref devdata);
                        }
                    }
                    SetupDiDestroyDeviceInfoList(hdev);
                }

                // Step 3: Bind the driver to the newly spawned hardware node
                bool result = UpdateDriverForPlugAndPlayDevices(
                    IntPtr.Zero,
                    hwid,
                    infPath,
                    1, // INSTALLFLAG_FORCE
                    out bool rebootRequired);

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] User denied admin rights or install failed: {ex.Message}");
                return false;
            }
        }

        public MMDevice? GetVirtualSpeakerDevice()
        {
            var enumerator = new MMDeviceEnumerator();
            
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            // Search for common industry-standard virtual audio cables
            var virtualDevice = devices.FirstOrDefault(d => 
                d.FriendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase) || 
                d.FriendlyName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
                d.FriendlyName.Contains("VAC", StringComparison.OrdinalIgnoreCase) ||
                d.FriendlyName.Contains("Virtual Cable", StringComparison.OrdinalIgnoreCase) ||
                d.FriendlyName.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
            );

            return virtualDevice;
        }
    }
}
