package main

import (
	"bufio"
	"crypto/tls"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/httputil"
	"os"
	"time"
)

type configuration struct {
	Backend      string
	Port         int
	LoopbackOnly bool
	Token        string
	Certificate  string
	Key          string
}

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}

func run() error {
	reader := bufio.NewReader(os.Stdin)
	line, err := reader.ReadBytes('\n')
	if err != nil {
		return err
	}
	var config configuration
	if err := json.Unmarshal(line, &config); err != nil {
		return err
	}
	clear(line)
	certificate, err := tls.X509KeyPair([]byte(config.Certificate), []byte(config.Key))
	if err != nil {
		return err
	}
	config.Key = ""
	local, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		return err
	}
	defer local.Close()
	secure, err := tls.Listen("tcp4", fmt.Sprintf("%s:%d", listenHost(config), config.Port), &tls.Config{
		Certificates: []tls.Certificate{certificate}, MinVersion: tls.VersionTLS12,
		ClientAuth: tls.RequireAnyClientCert, VerifyConnection: validClient,
	})
	if err != nil {
		return err
	}
	defer secure.Close()
	backend := inboundProxy(config)
	outbound := &outboundHandler{config: config, certificate: certificate, cache: make(map[string]cachedTransport)}
	localServer := &http.Server{Handler: outbound, ReadHeaderTimeout: 10 * time.Second, IdleTimeout: 30 * time.Second}
	peerServer := &http.Server{Handler: backend, ReadHeaderTimeout: 10 * time.Second, IdleTimeout: 30 * time.Second}
	defer localServer.Close()
	defer peerServer.Close()
	finished := make(chan error, 2)
	go func() { finished <- localServer.Serve(local) }()
	go func() { finished <- peerServer.Serve(secure) }()
	go func() { _, _ = io.Copy(io.Discard, reader); finished <- nil }()
	fmt.Println(local.Addr().(*net.TCPAddr).Port)
	return <-finished
}

func listenHost(config configuration) string {
	if config.LoopbackOnly {
		return "127.0.0.1"
	}
	return "0.0.0.0"
}

func proxyError(writer http.ResponseWriter, _ *http.Request, err error) {
	writer.Header().Set("Content-Type", "application/json; charset=utf-8")
	writer.WriteHeader(http.StatusBadGateway)
	_ = json.NewEncoder(writer).Encode(map[string]string{"error": err.Error()})
}

func inboundProxy(config configuration) http.Handler {
	proxy := &httputil.ReverseProxy{Rewrite: func(request *httputil.ProxyRequest) {
		request.Out.URL.Scheme = "http"
		request.Out.URL.Host = config.Backend
		request.Out.Host = config.Backend
		stripHeaders(request.Out.Header)
		request.Out.Header.Set("X-FileSync-Bridge", config.Token)
		request.Out.Header.Set("X-FileSync-Fingerprint", fingerprint(request.In.TLS.PeerCertificates[0]))
		host, _, _ := net.SplitHostPort(request.In.RemoteAddr)
		request.Out.Header.Set("X-FileSync-Address", host)
	}, ErrorHandler: proxyError, FlushInterval: -1}
	return http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		if !peerPath(request.URL.Path) {
			http.NotFound(writer, request)
			return
		}
		if err := validClient(*request.TLS); err != nil {
			http.Error(writer, err.Error(), http.StatusForbidden)
			return
		}
		proxy.ServeHTTP(writer, request)
	})
}
