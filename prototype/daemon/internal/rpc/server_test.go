package rpc

import (
	"bytes"
	"context"
	"encoding/json"
	"net"
	"testing"
	"time"
)

// dialTest starts srv on a random TCP port, returns a conn for sending requests.
func dialTest(t *testing.T, srv *Server) net.Conn {
	t.Helper()
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen: %v", err)
	}

	ctx, cancel := context.WithCancel(context.Background())
	t.Cleanup(func() {
		cancel()
		ln.Close()
	})

	go srv.Listen(ctx, ln) //nolint:errcheck

	conn, err := net.DialTimeout("tcp", ln.Addr().String(), 2*time.Second)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	t.Cleanup(func() { conn.Close() })
	return conn
}

func sendRequest(t *testing.T, conn net.Conn, req Request) Response {
	t.Helper()
	data, err := json.Marshal(req)
	if err != nil {
		t.Fatalf("marshal request: %v", err)
	}
	data = append(data, '\n')

	if _, err := conn.Write(data); err != nil {
		t.Fatalf("write request: %v", err)
	}

	conn.SetReadDeadline(time.Now().Add(2 * time.Second))
	var resp Response
	dec := json.NewDecoder(bytes.NewReader(readOne(t, conn)))
	if err := dec.Decode(&resp); err != nil {
		t.Fatalf("decode response: %v", err)
	}
	return resp
}

func readOne(t *testing.T, conn net.Conn) []byte {
	t.Helper()
	buf := make([]byte, 4096)
	n, err := conn.Read(buf)
	if err != nil {
		t.Fatalf("read response: %v", err)
	}
	return buf[:n]
}

func rawID(v interface{}) *json.RawMessage {
	b, _ := json.Marshal(v)
	r := json.RawMessage(b)
	return &r
}

func TestServer_EchoMethod(t *testing.T) {
	srv := NewServer(nil)
	srv.Register("echo", func(ctx context.Context, params json.RawMessage) (interface{}, error) {
		return json.RawMessage(params), nil
	})

	conn := dialTest(t, srv)
	params, _ := json.Marshal(map[string]string{"msg": "hello"})
	p := json.RawMessage(params)
	resp := sendRequest(t, conn, Request{
		JSONRPC: "2.0",
		ID:      rawID(1),
		Method:  "echo",
		Params:  p,
	})

	if resp.Error != nil {
		t.Fatalf("unexpected error: %v", resp.Error)
	}
	if resp.Result == nil {
		t.Fatal("expected non-nil result")
	}
}

func TestServer_MethodNotFound(t *testing.T) {
	srv := NewServer(nil)
	conn := dialTest(t, srv)

	resp := sendRequest(t, conn, Request{
		JSONRPC: "2.0",
		ID:      rawID(2),
		Method:  "no.such.method",
	})

	if resp.Error == nil {
		t.Fatal("expected error for unknown method")
	}
	if resp.Error.Code != ErrCodeMethodNotFound {
		t.Errorf("error code = %d, want %d", resp.Error.Code, ErrCodeMethodNotFound)
	}
}

func TestServer_InvalidVersion(t *testing.T) {
	srv := NewServer(nil)
	conn := dialTest(t, srv)

	resp := sendRequest(t, conn, Request{
		JSONRPC: "1.0",
		ID:      rawID(3),
		Method:  "echo",
	})

	if resp.Error == nil {
		t.Fatal("expected error for invalid JSON-RPC version")
	}
	if resp.Error.Code != ErrCodeInvalidRequest {
		t.Errorf("error code = %d, want %d", resp.Error.Code, ErrCodeInvalidRequest)
	}
}

func TestServer_HandlerError(t *testing.T) {
	srv := NewServer(nil)
	srv.Register("fail", func(ctx context.Context, params json.RawMessage) (interface{}, error) {
		return nil, &RPCError{Code: ErrCodeInternal, Message: "intentional failure"}
	})

	conn := dialTest(t, srv)
	resp := sendRequest(t, conn, Request{
		JSONRPC: "2.0",
		ID:      rawID(4),
		Method:  "fail",
	})

	if resp.Error == nil {
		t.Fatal("expected error response")
	}
	if resp.Error.Code != ErrCodeInternal {
		t.Errorf("error code = %d, want %d", resp.Error.Code, ErrCodeInternal)
	}
}

func TestServer_MultipleRequests(t *testing.T) {
	srv := NewServer(nil)
	counter := 0
	srv.Register("count", func(ctx context.Context, params json.RawMessage) (interface{}, error) {
		counter++
		return counter, nil
	})

	conn := dialTest(t, srv)

	for i := 1; i <= 3; i++ {
		resp := sendRequest(t, conn, Request{
			JSONRPC: "2.0",
			ID:      rawID(i),
			Method:  "count",
		})
		if resp.Error != nil {
			t.Fatalf("request %d error: %v", i, resp.Error)
		}
	}
	if counter != 3 {
		t.Errorf("counter = %d, want 3", counter)
	}
}
