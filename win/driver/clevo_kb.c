/*
 * clevo_kb.c - Clevo white keyboard backlight driver for Windows
 *
 * Calls acpi_evaluate_dsm() on the CLV0001 ACPI device with:
 *   UUID:     93f224e4-fbdc-4bbf-add6-db71bdc0afad
 *   Revision: 0
 *   Function: cmd  (0x27 = set brightness, 0x3D = get brightness)
 *   Arg:      brightness value 0-5 (package containing one integer)
 *
 * Exposes \\.\ClevoKbBacklight so user-mode can call DeviceIoControl.
 *
 * Build: requires WDK + KMDF. See clevo_kb.vcxproj / build.ps1.
 */

#include <ntddk.h>
#include <wdm.h>
#include <wdf.h>
#include <acpiioct.h>
#include <initguid.h>

/* ── IOCTL codes ──────────────────────────────────────────────────────────── */
/* CTL_CODE(FILE_DEVICE_UNKNOWN=0x22, 0x800, METHOD_BUFFERED, FILE_ANY_ACCESS) */
#define IOCTL_CLEVOKB_SET_LEVEL  CTL_CODE(0x22, 0x800, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_CLEVOKB_GET_LEVEL  CTL_CODE(0x22, 0x801, METHOD_BUFFERED, FILE_ANY_ACCESS)

/* ── Clevo ACPI DSM constants (from clevo_interfaces.h) ──────────────────── */
/* {93f224e4-fbdc-4bbf-add6-db71bdc0afad} */
DEFINE_GUID(CLEVO_ACPI_DSM_UUID,
    0x93f224e4, 0xfbdc, 0x4bbf,
    0xad, 0xd6, 0xdb, 0x71, 0xbd, 0xc0, 0xaf, 0xad);

#define CLEVO_CMD_SET_KB_WHITE_LEDS  0x27
#define CLEVO_CMD_GET_KB_WHITE_LEDS  0x3D
#define CLEVO_ACPI_RESOURCE_HID      L"CLV0001"

/* ── Device context ───────────────────────────────────────────────────────── */
typedef struct _DEVICE_CONTEXT {
    ACPI_EVAL_INPUT_BUFFER_EX   DsmInput;
    WDFDEVICE                   Device;
    ACPI_INTERFACE_STANDARD2    AcpiInterface;
} DEVICE_CONTEXT, *PDEVICE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_CONTEXT, GetDeviceContext)

/* ── Forward declarations ─────────────────────────────────────────────────── */
DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD ClevoKbDeviceAdd;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL ClevoKbIoDeviceControl;

NTSTATUS ClevoEvalDsm(WDFDEVICE Device, ULONG Cmd, ULONG Arg, ULONG *Result);

/* ── DriverEntry ──────────────────────────────────────────────────────────── */
NTSTATUS DriverEntry(
    _In_ PDRIVER_OBJECT  DriverObject,
    _In_ PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, ClevoKbDeviceAdd);
    return WdfDriverCreate(DriverObject, RegistryPath,
                           WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
}

/* ── DeviceAdd ────────────────────────────────────────────────────────────── */
NTSTATUS ClevoKbDeviceAdd(
    _In_    WDFDRIVER       Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit)
{
    UNREFERENCED_PARAMETER(Driver);

    NTSTATUS status;
    WDFDEVICE device;
    WDF_OBJECT_ATTRIBUTES deviceAttributes;
    WDF_IO_QUEUE_CONFIG    queueConfig;
    WDFQUEUE               queue;
    UNICODE_STRING         devName, symLink;

    /* Named device object so user-mode can open it */
    RtlInitUnicodeString(&devName, L"\\Device\\ClevoKbBacklight");
    status = WdfDeviceInitAssignName(DeviceInit, &devName);
    if (!NT_SUCCESS(status)) return status;

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&deviceAttributes, DEVICE_CONTEXT);
    status = WdfDeviceCreate(&DeviceInit, &deviceAttributes, &device);
    if (!NT_SUCCESS(status)) return status;

    /* Symbolic link so user-mode can open \\.\ClevoKbBacklight */
    RtlInitUnicodeString(&symLink, L"\\DosDevices\\ClevoKbBacklight");
    status = WdfDeviceCreateSymbolicLink(device, &symLink);
    if (!NT_SUCCESS(status)) return status;

    /* Default I/O queue */
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig,
                                           WdfIoQueueDispatchSequential);
    queueConfig.EvtIoDeviceControl = ClevoKbIoDeviceControl;
    status = WdfIoQueueCreate(device, &queueConfig,
                              WDF_NO_OBJECT_ATTRIBUTES, &queue);
    return status;
}

/* ── IOCTL handler ────────────────────────────────────────────────────────── */
VOID ClevoKbIoDeviceControl(
    _In_ WDFQUEUE   Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t     OutputBufferLength,
    _In_ size_t     InputBufferLength,
    _In_ ULONG      IoControlCode)
{
    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);

    NTSTATUS  status = STATUS_INVALID_DEVICE_REQUEST;
    WDFDEVICE device = WdfIoQueueGetDevice(Queue);
    ULONG     result = 0;
    ULONG_PTR bytesWritten = 0;

    switch (IoControlCode)
    {
    case IOCTL_CLEVOKB_SET_LEVEL:
    {
        ULONG *inBuf = NULL;
        size_t inLen = 0;
        status = WdfRequestRetrieveInputBuffer(Request, sizeof(ULONG),
                                               (PVOID*)&inBuf, &inLen);
        if (!NT_SUCCESS(status)) break;

        ULONG level = *inBuf;
        if (level > 5) { status = STATUS_INVALID_PARAMETER; break; }

        status = ClevoEvalDsm(device, CLEVO_CMD_SET_KB_WHITE_LEDS,
                              level, &result);
        break;
    }
    case IOCTL_CLEVOKB_GET_LEVEL:
    {
        ULONG *outBuf = NULL;
        size_t outLen = 0;
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(ULONG),
                                                (PVOID*)&outBuf, &outLen);
        if (!NT_SUCCESS(status)) break;

        status = ClevoEvalDsm(device, CLEVO_CMD_GET_KB_WHITE_LEDS,
                              0, &result);
        if (NT_SUCCESS(status)) {
            *outBuf = result;
            bytesWritten = sizeof(ULONG);
        }
        break;
    }
    }

    WdfRequestCompleteWithInformation(Request, status, bytesWritten);
}

/* ── DSM evaluation ───────────────────────────────────────────────────────── */
/*
 * Evaluates ACPI _DSM on the CLV0001 device:
 *   Arg0 = UUID  {93f224e4-...}
 *   Arg1 = RevisionId = 0
 *   Arg2 = FunctionIndex = Cmd  (e.g. 0x27)
 *   Arg3 = Package { Integer(Arg) }
 *
 * This mirrors what clevo_acpi.c does with acpi_evaluate_dsm().
 *
 * On Windows we send IOCTL_ACPI_EVAL_METHOD_EX to the ACPI driver
 * stack beneath our device, with an ACPI_EVAL_INPUT_BUFFER_COMPLEX_EX
 * containing the four _DSM arguments.
 */
NTSTATUS ClevoEvalDsm(
    _In_  WDFDEVICE Device,
    _In_  ULONG     Cmd,
    _In_  ULONG     Arg,
    _Out_ ULONG    *Result)
{
    *Result = 0;

    /*
     * ACPI_EVAL_INPUT_BUFFER_COMPLEX_EX layout:
     *
     *   [0]  Signature        ULONG  = ACPI_EVAL_INPUT_BUFFER_COMPLEX_EX_SIGNATURE
     *   [4]  MethodName[256]  CHAR[] = "_DSM\0"
     *  [260]  ArgumentCount   ULONG  = 4
     *
     *  Arg0: Buffer (UUID bytes, 16 bytes)
     *    Type       USHORT = ACPI_METHOD_ARGUMENT_BUFFER
     *    DataLength USHORT = 16
     *    Data       BYTE[16]
     *    (padded to ULONG alignment: total 4+16 = 20 bytes)
     *
     *  Arg1: Integer (RevisionId = 0)
     *    Type       USHORT = ACPI_METHOD_ARGUMENT_INTEGER
     *    DataLength USHORT = 4
     *    Data       ULONG  = 0
     *    total: 8 bytes
     *
     *  Arg2: Integer (FunctionIndex = Cmd)
     *    same layout, Data = Cmd
     *    total: 8 bytes
     *
     *  Arg3: Package containing one integer
     *    Type       USHORT = ACPI_METHOD_ARGUMENT_PACKAGE_EX
     *    DataLength USHORT = 8  (size of one nested integer argument)
     *    nested Arg: Type=INTEGER, DataLength=4, Data=Arg
     *    total: 4 + 8 = 12 bytes
     *
     * Grand total input buffer: 264 + 20 + 8 + 8 + 12 = 312 bytes
     */

#define CLEVOKB_INBUF_SIZE  312
#define CLEVOKB_OUTBUF_SIZE 64

    /* Use a heap buffer; stack space in kernel is limited */
    PUCHAR inBuf = (PUCHAR)ExAllocatePoolWithTag(NonPagedPool,
                       CLEVOKB_INBUF_SIZE, 'bKvC');
    if (!inBuf) return STATUS_INSUFFICIENT_RESOURCES;
    RtlZeroMemory(inBuf, CLEVOKB_INBUF_SIZE);

    PUCHAR outBuf = (PUCHAR)ExAllocatePoolWithTag(NonPagedPool,
                        CLEVOKB_OUTBUF_SIZE, 'bKvC');
    if (!outBuf) {
        ExFreePoolWithTag(inBuf, 'bKvC');
        return STATUS_INSUFFICIENT_RESOURCES;
    }
    RtlZeroMemory(outBuf, CLEVOKB_OUTBUF_SIZE);

    /* Signature */
    *(PULONG)(inBuf + 0) = ACPI_EVAL_INPUT_BUFFER_COMPLEX_EX_SIGNATURE;

    /* MethodName = "_DSM" */
    inBuf[4] = '_'; inBuf[5] = 'D'; inBuf[6] = 'S'; inBuf[7] = 'M';

    /* ArgumentCount = 4 */
    *(PULONG)(inBuf + 260) = 4;

    ULONG offset = 264;

    /* Arg0: Buffer (UUID, 16 bytes) */
    *(PUSHORT)(inBuf + offset)     = ACPI_METHOD_ARGUMENT_BUFFER; /* Type */
    *(PUSHORT)(inBuf + offset + 2) = 16;                           /* DataLength */
    /* UUID in little-endian wire format */
    GUID uuid = CLEVO_ACPI_DSM_UUID;
    RtlCopyMemory(inBuf + offset + 4, &uuid, 16);
    offset += 4 + 16;  /* = 284 */

    /* Arg1: Integer (RevisionId = 0) */
    *(PUSHORT)(inBuf + offset)     = ACPI_METHOD_ARGUMENT_INTEGER;
    *(PUSHORT)(inBuf + offset + 2) = 4;
    *(PULONG) (inBuf + offset + 4) = 0;
    offset += 8;  /* = 292 */

    /* Arg2: Integer (FunctionIndex = Cmd) */
    *(PUSHORT)(inBuf + offset)     = ACPI_METHOD_ARGUMENT_INTEGER;
    *(PUSHORT)(inBuf + offset + 2) = 4;
    *(PULONG) (inBuf + offset + 4) = Cmd;
    offset += 8;  /* = 300 */

    /* Arg3: Package containing one integer */
    *(PUSHORT)(inBuf + offset)     = ACPI_METHOD_ARGUMENT_PACKAGE_EX;
    *(PUSHORT)(inBuf + offset + 2) = 8;  /* DataLength = size of nested arg */
    /* Nested integer argument */
    *(PUSHORT)(inBuf + offset + 4) = ACPI_METHOD_ARGUMENT_INTEGER;
    *(PUSHORT)(inBuf + offset + 6) = 4;
    *(PULONG) (inBuf + offset + 8) = Arg;
    offset += 12;  /* = 312 */

    /* Send IOCTL_ACPI_EVAL_METHOD_EX down the device stack */
    WDFIOTARGET ioTarget = WdfDeviceGetIoTarget(Device);
    WDF_MEMORY_DESCRIPTOR inputDesc, outputDesc;
    WDF_MEMORY_DESCRIPTOR_INIT_BUFFER(&inputDesc,  inBuf,  CLEVOKB_INBUF_SIZE);
    WDF_MEMORY_DESCRIPTOR_INIT_BUFFER(&outputDesc, outBuf, CLEVOKB_OUTBUF_SIZE);

    NTSTATUS status = WdfIoTargetSendIoctlSynchronously(
        ioTarget,
        WDF_NO_HANDLE,
        IOCTL_ACPI_EVAL_METHOD_EX,
        &inputDesc,
        &outputDesc,
        NULL,   /* request options */
        NULL);  /* bytes returned */

    if (NT_SUCCESS(status)) {
        /* ACPI_EVAL_OUTPUT_BUFFER: Sig(4) Len(4) Count(4) Arg[0].Type(2) Arg[0].DataLen(2) Arg[0].Data(4) */
        if (CLEVOKB_OUTBUF_SIZE >= 20)
            *Result = *(PULONG)(outBuf + 16);
    }

    ExFreePoolWithTag(inBuf,  'bKvC');
    ExFreePoolWithTag(outBuf, 'bKvC');
    return status;
}
