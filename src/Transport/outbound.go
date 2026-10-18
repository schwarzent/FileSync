package main

import (
	"crypto/sha256"
	"crypto/subtle"
	"crypto/tls"
	"crypto/x509"
	"encoding/hex"
	"errors"
	"net"
	"net/http"
	"net/http/httputil"
	"strings"
	"sync"
	"time"
)

type outboundHandler struct {
	config      configuration
	certificate tls.Certificate
	mutex       sync.Mutex
	cache       map[string]cachedTransport
}

const idleLifetime = 30 * time.Second

type cachedTransport struct {
	transport *http.Transport
	used      time.Time
}

func fingerprint(certificate *x509.Certificate) string {
	digest := sha256.Sum256(certificate.Raw)
	return strings.ToUpper(hex.EncodeToString(digest[:]))
}

func validClient(connection tls.ConnectionState) error {
	if len(connection.PeerCertificates) != 1 {
		return errors.New("设备证书数量无效")
	}
	certificate := connection.PeerCertificates[0]
	if time.Now().Before(certificate.NotBefore) || time.Now().After(certificate.NotAfter) {
		return errors.New("设备证书已过期或尚未生效")
	}
	return nil
}

func stripHeaders(headers http.Header) {
	for key := range headers {
		if strings.HasPrefix(strings.ToLower(key), "x-filesync-") {
			headers.Del(key)
		}
	}
}

func peerPath(path string) bool { return strings.HasPrefix(path, "/peer/") }

func validTarget(target string) bool {
	host, port, err := net.SplitHostPort(target)
	if err != nil || port == "" {
		return false
	}
	ip := net.ParseIP(host)
	return ip != nil && ip.To4() != nil && (ip.IsPrivate() || ip.IsLoopback() || ip.IsLinkLocalUnicast())
}

func (handler *outboundHandler) ServeHTTP(writer http.ResponseWriter, request *http.Request) {
	if subtle.ConstantTimeCompare([]byte(request.Header.Get("X-FileSync-Bridge")), []byte(handler.config.Token)) != 1 {
		http.Error(writer, "本机通信授权无效", http.StatusForbidden)
		return
	}
	target := request.Header.Get("X-FileSync-Target")
	pin := request.Header.Get("X-FileSync-Pin")
	if !peerPath(request.URL.Path) {
		http.NotFound(writer, request)
		return
	}
	if !validTarget(target) || len(pin) != sha256.Size*2 {
		http.Error(writer, "远端设备地址或证书指纹无效", http.StatusBadRequest)
		return
	}
	transport := handler.connection(target, pin)
	proxy := &httputil.ReverseProxy{Transport: transport, Rewrite: func(proxyRequest *httputil.ProxyRequest) {
		proxyRequest.Out.URL.Scheme = "https"
		proxyRequest.Out.URL.Host = target
		proxyRequest.Out.Host = target
		stripHeaders(proxyRequest.Out.Header)
	}, ErrorHandler: proxyError, FlushInterval: -1}
	proxy.ServeHTTP(writer, request)
}

func (handler *outboundHandler) connection(target string, pin string) *http.Transport {
	handler.mutex.Lock()
	defer handler.mutex.Unlock()
	key := target + "/" + pin
	for name, cached := range handler.cache {
		if time.Since(cached.used) > idleLifetime {
			cached.transport.CloseIdleConnections()
			delete(handler.cache, name)
		}
	}
	if cached, ok := handler.cache[key]; ok {
		handler.cache[key] = cachedTransport{cached.transport, time.Now()}
		return cached.transport
	}
	transport := handler.newConnection(pin)
	handler.cache[key] = cachedTransport{transport, time.Now()}
	return transport
}

func (handler *outboundHandler) newConnection(pin string) *http.Transport {
	return &http.Transport{
		Proxy: nil, DialContext: (&net.Dialer{Timeout: 10 * time.Second}).DialContext,
		IdleConnTimeout: idleLifetime, TLSHandshakeTimeout: 10 * time.Second,
		TLSClientConfig: &tls.Config{
			Certificates: []tls.Certificate{handler.certificate}, MinVersion: tls.VersionTLS12,
			// 自签名设备采用配对指纹验证，代替公共 CA 链与主机名验证。
			InsecureSkipVerify: true,
			VerifyConnection: func(connection tls.ConnectionState) error {
				if err := validClient(connection); err != nil {
					return err
				}
				if fingerprint(connection.PeerCertificates[0]) != pin {
					return errors.New("设备证书指纹不匹配")
				}
				return nil
			},
		},
	}
}
