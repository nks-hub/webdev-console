// devforge CLI — sends JSON-RPC commands to the running DevForge daemon.
package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"net"
	"os"
	"time"

	"github.com/nks/devforge/internal/rpc"
)

func main() {
	pipePath := flag.String("pipe", "", "pipe/socket path (default: platform default)")
	flag.Usage = func() {
		fmt.Fprintf(os.Stderr, "Usage: devforge [flags] <command> [args]\n\nCommands:\n")
		fmt.Fprintf(os.Stderr, "  status                   Show daemon and service status\n")
		fmt.Fprintf(os.Stderr, "  site create <file.toml>  Create a site from a TOML config file\n")
		fmt.Fprintf(os.Stderr, "  service start <name>     Start a registered service\n")
		fmt.Fprintf(os.Stderr, "  service stop <name>      Stop a running service\n")
		fmt.Fprintf(os.Stderr, "  service restart <name>   Restart a service\n\n")
		flag.PrintDefaults()
	}
	flag.Parse()

	args := flag.Args()
	if len(args) == 0 {
		flag.Usage()
		os.Exit(1)
	}

	conn, err := rpc.DialPipe(*pipePath)
	if err != nil {
		log.Fatalf("cannot connect to daemon: %v\n(is devforge daemon running?)", err)
	}
	defer conn.Close()

	switch args[0] {
	case "status":
		callAndPrint(conn, "daemon.status", nil)

	case "site":
		if len(args) < 3 || args[1] != "create" {
			fmt.Fprintln(os.Stderr, "usage: devforge site create <file.toml>")
			os.Exit(1)
		}
		tomlData, err := os.ReadFile(args[2])
		if err != nil {
			log.Fatalf("reading TOML file: %v", err)
		}
		params := map[string]string{"toml": string(tomlData)}
		callAndPrint(conn, "site.create", params)

	case "service":
		if len(args) < 3 {
			fmt.Fprintln(os.Stderr, "usage: devforge service <start|stop|restart> <name>")
			os.Exit(1)
		}
		method := "service." + args[1]
		params := map[string]string{"name": args[2]}
		callAndPrint(conn, method, params)

	default:
		fmt.Fprintf(os.Stderr, "unknown command: %s\n", args[0])
		flag.Usage()
		os.Exit(1)
	}
}

func callAndPrint(conn net.Conn, method string, params interface{}) {
	id := json.RawMessage(`1`)
	req := rpc.Request{
		JSONRPC: "2.0",
		ID:      &id,
		Method:  method,
	}
	if params != nil {
		b, err := json.Marshal(params)
		if err != nil {
			log.Fatalf("marshal params: %v", err)
		}
		req.Params = json.RawMessage(b)
	}

	data, _ := json.Marshal(req)
	data = append(data, '\n')
	if _, err := conn.Write(data); err != nil {
		log.Fatalf("send request: %v", err)
	}

	conn.SetReadDeadline(time.Now().Add(10 * time.Second))

	var resp rpc.Response
	dec := json.NewDecoder(conn)
	if err := dec.Decode(&resp); err != nil {
		log.Fatalf("decode response: %v", err)
	}

	if resp.Error != nil {
		fmt.Fprintf(os.Stderr, "error [%d]: %s\n", resp.Error.Code, resp.Error.Message)
		if resp.Error.Data != nil {
			fmt.Fprintf(os.Stderr, "detail: %v\n", resp.Error.Data)
		}
		os.Exit(1)
	}

	out, _ := json.MarshalIndent(resp.Result, "", "  ")
	fmt.Println(string(out))
}
