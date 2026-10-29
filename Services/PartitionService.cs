using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SystemMigrator.Models;

namespace SystemMigrator.Services
{
    /// <summary>
    /// 磁碟 / 分區列舉服務。
    /// 優先使用 IOCTL（可取得磁碟編號、分割表格式、EFI/MSR 等未掛載分區）；
    /// 若沒有管理員權限或查詢失敗，自動回退到 DriveInfo 只列已掛載的固定磁碟。
    /// </summary>
    public sealed class PartitionService
    {
        private const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
        private const uint IOCTL_DISK_GET_PARTITION_INFO_EX = 0x00070048;
        private const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
        private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;

        private const int PARTITION_STYLE_MBR = 0;
        private const int PARTITION_STYLE_GPT = 1;

        private static readonly Guid GptEfi = new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");
        private static readonly Guid GptMsr = new("E3C9E316-0B5C-4DB8-817D-F92DF00215AE");
        private static readonly Guid GptRecovery = new("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC");
        private static readonly Guid GptBasicData = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");

        /// <summary>日誌事件（供 UI 顯示查詢過程）。</summary>
        public event Action<string, LogLevel>? Log;

        /// <summary>目前行程是否具備管理員權限。</summary>
        public bool IsElevated { get; private set; }

        /// <summary>列舉所有固定磁碟上的分區。</summary>
        public IReadOnlyList<PartitionInfo> Enumerate()
        {
            IsElevated = CheckAdministrator();

            try
            {
                List<PartitionInfo> native = EnumerateNative();
                if (native.Count > 0) return native;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"原生磁碟查詢失敗（{ex.Message}），改用基本模式。", LogLevel.Error);
            }

            return EnumerateFallback();
        }

        // ==================== 原生查詢 ====================

        private List<PartitionInfo> EnumerateNative()
        {
            var result = new List<PartitionInfo>();
            Dictionary<(int Disk, long Offset), string> letters = MapDriveLetters();

            string systemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd(':').ToUpperInvariant();

            int missingDisks = 0;
            for (int diskNumber = 0; diskNumber < 16 && missingDisks < 2; diskNumber++)
            {
                using SafeFileHandle? diskHandle = OpenDevice($@"\\.\PhysicalDrive{diskNumber}", preferRead: true);
                if (diskHandle == null)
                {
                    missingDisks++;
                    continue;
                }

                missingDisks = 0;
                ulong diskSize = GetDiskSize(diskHandle);
                string model = GetDiskModel(diskHandle);
                int foundOnDisk = 0;

                for (int partitionNumber = 1; partitionNumber <= 128; partitionNumber++)
                {
                    using SafeFileHandle? partitionHandle =
                        OpenDevice($@"\\.\Harddisk{diskNumber}\Partition{partitionNumber}", preferRead: false);

                    if (partitionHandle == null) break; // Windows 的分割編號是連續的
                    if (!TryGetPartitionInfo(partitionHandle, out PartitionInfo? info)) continue;

                    foundOnDisk++;
                    info.DiskNumber = diskNumber;
                    info.DiskModel = model;
                    info.DiskSizeBytes = diskSize;
                    info.PartitionNumber = partitionNumber;

                    if (letters.TryGetValue((diskNumber, info.StartingOffset), out string? letter))
                    {
                        info.DriveLetter = letter;

                        try
                        {
                            var drive = new DriveInfo($"{letter}:");
                            if (drive.IsReady)
                            {
                                info.Label = drive.VolumeLabel;
                                info.FileSystem = drive.DriveFormat;
                                info.FreeBytes = (ulong)Math.Max(0, drive.AvailableFreeSpace);
                            }
                        }
                        catch
                        {
                            // 磁碟未就緒（例如離線磁碟）時忽略。
                        }
                    }

                    info.IsSystemPartition =
                        !string.IsNullOrEmpty(info.DriveLetter) &&
                        string.Equals(info.DriveLetter, systemDrive, StringComparison.OrdinalIgnoreCase);

                    result.Add(info);
                }

                if (foundOnDisk == 0) missingDisks = 2;
            }

            return result
                .OrderBy(p => p.DiskNumber)
                .ThenBy(p => p.StartingOffset)
                .ToList();
        }

        private static bool TryGetPartitionInfo(SafeFileHandle handle, out PartitionInfo info)
        {
            info = new PartitionInfo();

            int bufferSize = Marshal.SizeOf<PARTITION_INFORMATION_EX>();
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);

            try
            {
                if (!DeviceIoControl(
                        handle,
                        IOCTL_DISK_GET_PARTITION_INFO_EX,
                        IntPtr.Zero,
                        0,
                        buffer,
                        bufferSize,
                        out _,
                        IntPtr.Zero))
                {
                    return false;
                }

                PARTITION_INFORMATION_EX raw = Marshal.PtrToStructure<PARTITION_INFORMATION_EX>(buffer);

                info.StartingOffset = raw.StartingOffset;
                info.SizeBytes = (ulong)Math.Max(0, raw.PartitionLength);
                info.PartitionNumber = raw.PartitionNumber;

                if (raw.PartitionStyle == PARTITION_STYLE_GPT && raw.Union is { Length: >= 16 })
                {
                    info.Style = PartitionStyle.Gpt;
                    info.TypeGuid = new Guid(new ReadOnlySpan<byte>(raw.Union, 0, 16));

                    if (info.TypeGuid == GptEfi) info.Kind = PartitionKind.Efi;
                    else if (info.TypeGuid == GptMsr) info.Kind = PartitionKind.Msr;
                    else if (info.TypeGuid == GptRecovery) info.Kind = PartitionKind.Recovery;
                    else if (info.TypeGuid == GptBasicData) info.Kind = PartitionKind.BasicData;
                    else info.Kind = PartitionKind.Other;
                }
                else if (raw.PartitionStyle == PARTITION_STYLE_MBR && raw.Union is { Length: >= 1 })
                {
                    info.Style = PartitionStyle.Mbr;
                    byte mbrType = raw.Union[0];
                    info.Kind = mbrType switch
                    {
                        0xEF => PartitionKind.Efi,
                        0x27 => PartitionKind.Recovery,
                        0x0C or 0x07 or 0x0B or 0x06 => PartitionKind.BasicData,
                        _ => PartitionKind.Other
                    };
                }
                else
                {
                    info.Style = PartitionStyle.Unknown;
                    info.Kind = PartitionKind.Unknown;
                }

                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static Dictionary<(int Disk, long Offset), string> MapDriveLetters()
        {
            var map = new Dictionary<(int, long), string>();

            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                string name = drive.Name; // 例如 "C:\"
                if (name.Length < 2 || name[1] != ':') continue;

                string letter = char.ToUpperInvariant(name[0]).ToString();

                try
                {
                    using SafeFileHandle? volumeHandle = OpenDevice($@"\\.\{letter}:", preferRead: false);
                    if (volumeHandle == null) continue;

                    if (TryGetVolumeExtents(volumeHandle, out int diskNumber, out long offset))
                    {
                        map[(diskNumber, offset)] = letter;
                    }
                }
                catch
                {
                    // 單一磁碟機查詢失敗不影響其他磁碟機。
                }
            }

            return map;
        }

        private static bool TryGetVolumeExtents(SafeFileHandle handle, out int diskNumber, out long offset)
        {
            diskNumber = -1;
            offset = 0;

            const int bufferSize = 128;
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);

            try
            {
                if (!DeviceIoControl(
                        handle,
                        IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                        IntPtr.Zero,
                        0,
                        buffer,
                        bufferSize,
                        out int returned,
                        IntPtr.Zero))
                {
                    return false;
                }

                int count = Marshal.ReadInt32(buffer, 0);
                if (count < 1 || returned < 8 + 24) return false;

                diskNumber = Marshal.ReadInt32(buffer, 8);   // Extent[0].DiskNumber
                offset = Marshal.ReadInt64(buffer, 16);      // Extent[0].StartingOffset
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static ulong GetDiskSize(SafeFileHandle handle)
        {
            const int bufferSize = 256;
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);

            try
            {
                if (!DeviceIoControl(
                        handle,
                        IOCTL_DISK_GET_DRIVE_GEOMETRY_EX,
                        IntPtr.Zero,
                        0,
                        buffer,
                        bufferSize,
                        out _,
                        IntPtr.Zero))
                {
                    return 0;
                }

                // DISK_GEOMETRY 佔 24 bytes，DiskSize 位於 offset 24。
                long size = Marshal.ReadInt64(buffer, 24);
                return (ulong)Math.Max(0, size);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static string GetDiskModel(SafeFileHandle handle)
        {
            const int inputSize = 12;
            const int outputSize = 1024;

            IntPtr input = Marshal.AllocHGlobal(inputSize);
            IntPtr output = Marshal.AllocHGlobal(outputSize);

            try
            {
                Marshal.WriteInt32(input, 0, 0);  // StorageDeviceProperty
                Marshal.WriteInt32(input, 4, 0);  // PropertyStandardQuery
                Marshal.WriteInt32(input, 8, 0);

                if (!DeviceIoControl(
                        handle,
                        IOCTL_STORAGE_QUERY_PROPERTY,
                        input,
                        inputSize,
                        output,
                        outputSize,
                        out int returned,
                        IntPtr.Zero))
                {
                    return "未知型號";
                }

                string vendor = ReadAnsiString(output, returned, 12);
                string product = ReadAnsiString(output, returned, 16);
                string model = $"{vendor} {product}".Trim();

                return string.IsNullOrWhiteSpace(model) ? "未知型號" : model;
            }
            finally
            {
                Marshal.FreeHGlobal(input);
                Marshal.FreeHGlobal(output);
            }
        }

        private static string ReadAnsiString(IntPtr buffer, int totalLength, int offset)
        {
            if (offset <= 0 || offset >= totalLength) return "";

            var builder = new StringBuilder();
            for (int i = offset; i < totalLength; i++)
            {
                byte value = Marshal.ReadByte(buffer, i);
                if (value == 0) break;
                builder.Append((char)value);
            }

            return builder.ToString().Trim();
        }

        private static SafeFileHandle? OpenDevice(string path, bool preferRead)
        {
            uint[] accessModes = preferRead
                ? new[] { GENERIC_READ, 0u }
                : new[] { 0u, GENERIC_READ };

            foreach (uint access in accessModes)
            {
                SafeFileHandle handle = CreateFile(
                    path,
                    access,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    0,
                    IntPtr.Zero);

                if (!handle.IsInvalid) return handle;

                handle.Dispose();
            }

            return null;
        }

        // ==================== 回退模式 ====================

        private List<PartitionInfo> EnumerateFallback()
        {
            Log?.Invoke("以基本模式列舉磁碟（僅顯示已掛載磁碟機，缺少磁碟編號與 EFI 分割資訊）。", LogLevel.Info);

            var result = new List<PartitionInfo>();
            string systemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd(':').ToUpperInvariant();

            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || drive.Name.Length < 2) continue;

                try
                {
                    if (!drive.IsReady) continue;

                    string letter = char.ToUpperInvariant(drive.Name[0]).ToString();
                    result.Add(new PartitionInfo
                    {
                        DiskNumber = -1,
                        DiskModel = "未知型號（需要管理員權限）",
                        PartitionNumber = 0,
                        DriveLetter = letter,
                        Label = drive.VolumeLabel,
                        FileSystem = drive.DriveFormat,
                        Style = PartitionStyle.Unknown,
                        Kind = PartitionKind.BasicData,
                        SizeBytes = (ulong)Math.Max(0, drive.TotalSize),
                        FreeBytes = (ulong)Math.Max(0, drive.AvailableFreeSpace),
                        IsSystemPartition = string.Equals(letter, systemDrive, StringComparison.OrdinalIgnoreCase)
                    });
                }
                catch
                {
                    // 略過無法讀取的分割。
                }
            }

            return result;
        }

        private static bool CheckAdministrator()
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        // ==================== P/Invoke ====================

        [StructLayout(LayoutKind.Sequential)]
        private struct PARTITION_INFORMATION_EX
        {
            public int PartitionStyle;
            public int Padding;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 112)]
            public byte[] Union;

            public long StartingOffset;
            public long PartitionLength;
            public int PartitionNumber;

            [MarshalAs(UnmanagedType.U1)]
            public bool RewritePartition;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint ioControlCode,
            IntPtr inBuffer,
            int inBufferSize,
            IntPtr outBuffer,
            int outBufferSize,
            out int bytesReturned,
            IntPtr overlapped);
    }
}
