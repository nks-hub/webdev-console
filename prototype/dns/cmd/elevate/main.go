// UAC elevation helper for NKS WebDev Console on Windows.
//
// Compile:
//
//	go build -o elevate.exe ./cmd/elevate
//
// Usage:
//
//	elevate.exe <program> [args...]
//	elevate.exe -- powershell.exe -File hosts_manager.ps1 -Action add -Domain myapp.test
package main

import (
	"fmt"
	"os"
	"strings"
	"syscall"
	"unsafe"
)

// ---------------------------------------------------------------------------
// Windows API bindings
// ---------------------------------------------------------------------------

var (
	modShell32          = syscall.NewLazyDLL("shell32.dll")
	procShellExecuteEx  = modShell32.NewProc("ShellExecuteExW")

	modKernel32       = syscall.NewLazyDLL("kernel32.dll")
	procWaitForSingle = modKernel32.NewProc("WaitForSingleObject")
	procGetExitCode   = modKernel32.NewProc("GetExitCodeProcess")
	procCloseHandle   = modKernel32.NewProc("CloseHandle")

)

const (
	seeMaskNocloseProc = 0x00000040
	waitInfinite       = 0xFFFFFFFF
)

// shellExecuteInfo mirrors SHELLEXECUTEINFOW.
// https://docs.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-shellexecuteinfow
type shellExecuteInfo struct {
	cbSize         uint32
	fMask          uint32
	hwnd           uintptr
	lpVerb         *uint16
	lpFile         *uint16
	lpParameters   *uint16
	lpDirectory    *uint16
	nShow          int32
	hInstApp       uintptr
	lpIDList       uintptr
	lpClass        *uint16
	hkeyClass      uintptr
	dwHotKey       uint32
	hIconOrMonitor uintptr
	hProcess       syscall.Handle
}

// ---------------------------------------------------------------------------
// Elevation
// ---------------------------------------------------------------------------

// RunElevated launches exe with args under the "runas" verb (UAC prompt) and
// waits for the child to exit.  Returns the child's exit code.
func RunElevated(exe string, args []string) (int, error) {
	verbPtr, err := syscall.UTF16PtrFromString("runas")
	if err != nil {
		return 1, fmt.Errorf("encode verb: %w", err)
	}
	filePtr, err := syscall.UTF16PtrFromString(exe)
	if err != nil {
		return 1, fmt.Errorf("encode file: %w", err)
	}
	paramPtr, err := syscall.UTF16PtrFromString(buildParamString(args))
	if err != nil {
		return 1, fmt.Errorf("encode params: %w", err)
	}

	info := shellExecuteInfo{
		fMask:        seeMaskNocloseProc,
		lpVerb:       verbPtr,
		lpFile:       filePtr,
		lpParameters: paramPtr,
		nShow:        1, // SW_NORMAL
	}
	info.cbSize = uint32(unsafe.Sizeof(info))

	ret, _, callErr := procShellExecuteEx.Call(uintptr(unsafe.Pointer(&info)))
	if ret == 0 {
		return 1, fmt.Errorf("ShellExecuteEx: %w", callErr)
	}
	if info.hProcess == 0 {
		return 0, nil
	}
	defer closeHandle(info.hProcess)

	waitForSingleObject(info.hProcess)
	return getExitCode(info.hProcess), nil
}

func buildParamString(args []string) string {
	quoted := make([]string, len(args))
	for i, a := range args {
		if strings.ContainsAny(a, " \t") {
			quoted[i] = `"` + strings.ReplaceAll(a, `"`, `\"`) + `"`
		} else {
			quoted[i] = a
		}
	}
	return strings.Join(quoted, " ")
}

func waitForSingleObject(h syscall.Handle) {
	procWaitForSingle.Call(uintptr(h), waitInfinite)
}

func getExitCode(h syscall.Handle) int {
	var code uint32
	procGetExitCode.Call(uintptr(h), uintptr(unsafe.Pointer(&code)))
	return int(code)
}

func closeHandle(h syscall.Handle) {
	procCloseHandle.Call(uintptr(h))
}

// ---------------------------------------------------------------------------
// Admin check
// ---------------------------------------------------------------------------

func isAdmin() bool {
	token, err := syscall.OpenCurrentProcessToken()
	if err != nil {
		return false
	}
	defer token.Close()

	const tokenElevation = 20
	var elevation uint32
	var returnedLen uint32
	err = syscall.GetTokenInformation(
		token,
		tokenElevation,
		(*byte)(unsafe.Pointer(&elevation)),
		uint32(unsafe.Sizeof(elevation)),
		&returnedLen,
	)
	if err != nil {
		return false
	}
	return elevation != 0
}

// ---------------------------------------------------------------------------
// main
// ---------------------------------------------------------------------------

func main() {
	args := os.Args[1:]

	if len(args) == 0 {
		fmt.Fprintln(os.Stderr, "Usage: elevate.exe <program> [args...]")
		fmt.Fprintln(os.Stderr, "       elevate.exe -- powershell.exe -File hosts_manager.ps1 -Action add -Domain myapp.test")
		os.Exit(2)
	}

	if args[0] == "--" {
		args = args[1:]
	}
	if len(args) == 0 {
		fmt.Fprintln(os.Stderr, "No command specified after '--'")
		os.Exit(2)
	}

	if isAdmin() {
		fmt.Println("[elevate] Already running as administrator.")
		os.Exit(0)
	}

	program := args[0]
	programArgs := args[1:]
	fmt.Printf("[elevate] Requesting elevation for: %s %s\n", program, buildParamString(programArgs))

	exitCode, err := RunElevated(program, programArgs)
	if err != nil {
		fmt.Fprintf(os.Stderr, "[elevate] Error: %v\n", err)
		os.Exit(1)
	}
	os.Exit(exitCode)
}
