// Package rpc implements a JSON-RPC 2.0 server over a named pipe (Windows)
// or Unix domain socket (other platforms).
package rpc

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log/slog"
	"net"
	"sync"
)

// Request is a JSON-RPC 2.0 request object.
type Request struct {
	JSONRPC string          `json:"jsonrpc"`
	ID      *json.RawMessage `json:"id,omitempty"`
	Method  string          `json:"method"`
	Params  json.RawMessage `json:"params,omitempty"`
}

// Response is a JSON-RPC 2.0 response object.
type Response struct {
	JSONRPC string           `json:"jsonrpc"`
	ID      *json.RawMessage `json:"id"`
	Result  interface{}      `json:"result,omitempty"`
	Error   *RPCError        `json:"error,omitempty"`
}

// RPCError represents the JSON-RPC error object.
type RPCError struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
	Data    any    `json:"data,omitempty"`
}

func (e *RPCError) Error() string {
	return fmt.Sprintf("rpc error %d: %s", e.Code, e.Message)
}

// Standard JSON-RPC 2.0 error codes.
const (
	ErrCodeParse          = -32700
	ErrCodeInvalidRequest = -32600
	ErrCodeMethodNotFound = -32601
	ErrCodeInvalidParams  = -32602
	ErrCodeInternal       = -32603
)

// HandlerFunc is the signature for RPC method handlers.
// params is the raw JSON params field (may be nil).
// Returns a result value (must be JSON-serialisable) or an error.
type HandlerFunc func(ctx context.Context, params json.RawMessage) (interface{}, error)

// Server is the JSON-RPC 2.0 server.
type Server struct {
	mu       sync.RWMutex
	methods  map[string]HandlerFunc
	listener net.Listener
	logger   *slog.Logger
}

// NewServer creates a Server with the given logger. If logger is nil a default
// text logger writing to stderr is used.
func NewServer(logger *slog.Logger) *Server {
	if logger == nil {
		logger = slog.Default()
	}
	return &Server{
		methods: make(map[string]HandlerFunc),
		logger:  logger,
	}
}

// Register adds a handler for the given method name.
func (s *Server) Register(method string, h HandlerFunc) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.methods[method] = h
}

// Listen starts accepting connections on l. It blocks until ctx is cancelled
// or the listener is closed.
func (s *Server) Listen(ctx context.Context, l net.Listener) error {
	s.listener = l
	s.logger.Info("rpc server listening", "addr", l.Addr())

	go func() {
		<-ctx.Done()
		l.Close()
	}()

	for {
		conn, err := l.Accept()
		if err != nil {
			select {
			case <-ctx.Done():
				return nil
			default:
				return fmt.Errorf("accept: %w", err)
			}
		}
		go s.handleConn(ctx, conn)
	}
}

// Addr returns the listener address, or empty string if not yet listening.
func (s *Server) Addr() string {
	if s.listener == nil {
		return ""
	}
	return s.listener.Addr().String()
}

func (s *Server) handleConn(ctx context.Context, conn net.Conn) {
	defer conn.Close()
	remote := conn.RemoteAddr().String()
	s.logger.Debug("rpc connection opened", "remote", remote)

	dec := json.NewDecoder(conn)
	enc := json.NewEncoder(conn)

	for {
		var req Request
		if err := dec.Decode(&req); err != nil {
			if err == io.EOF {
				s.logger.Debug("rpc connection closed", "remote", remote)
				return
			}
			s.writeError(enc, nil, ErrCodeParse, "parse error", nil)
			return
		}

		if req.JSONRPC != "2.0" {
			s.writeError(enc, req.ID, ErrCodeInvalidRequest, "invalid JSON-RPC version", nil)
			continue
		}

		s.mu.RLock()
		handler, ok := s.methods[req.Method]
		s.mu.RUnlock()

		if !ok {
			s.writeError(enc, req.ID, ErrCodeMethodNotFound,
				fmt.Sprintf("method %q not found", req.Method), nil)
			continue
		}

		result, err := handler(ctx, req.Params)
		if err != nil {
			code := ErrCodeInternal
			if rpcErr, ok := err.(*RPCError); ok {
				s.writeError(enc, req.ID, rpcErr.Code, rpcErr.Message, rpcErr.Data)
				continue
			}
			s.writeError(enc, req.ID, code, err.Error(), nil)
			continue
		}

		resp := Response{
			JSONRPC: "2.0",
			ID:      req.ID,
			Result:  result,
		}
		if encErr := enc.Encode(resp); encErr != nil {
			s.logger.Warn("rpc encode response error", "err", encErr)
			return
		}
	}
}

func (s *Server) writeError(enc *json.Encoder, id *json.RawMessage, code int, msg string, data any) {
	resp := Response{
		JSONRPC: "2.0",
		ID:      id,
		Error: &RPCError{
			Code:    code,
			Message: msg,
			Data:    data,
		},
	}
	if err := enc.Encode(resp); err != nil {
		s.logger.Warn("rpc encode error response failed", "err", err)
	}
}
