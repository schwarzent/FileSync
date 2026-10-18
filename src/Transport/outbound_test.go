package main

import (
	"crypto/tls"
	"crypto/x509"
	"net/http"
	"testing"
	"time"
)

func TestTargetScope(t *testing.T) {
	for _, value := range []string{"8.8.8.8:443", "evil.example:443", "[::1]:443", "file:///secret"} {
		if validTarget(value) {
			t.Fatalf("接受了非局域网目标 %s", value)
		}
	}
	for _, value := range []string{"127.0.0.1:12345", "192.168.1.2:5379", "10.1.2.3:5379"} {
		if !validTarget(value) {
			t.Fatalf("拒绝了局域网目标 %s", value)
		}
	}
}

func TestPinsAndReuse(t *testing.T) {
	handler := &outboundHandler{cache: make(map[string]cachedTransport)}
	first := handler.connection("127.0.0.1:12345", "first")
	if handler.connection("127.0.0.1:12345", "first") != first {
		t.Fatal("同一设备连接未复用")
	}
	if handler.connection("127.0.0.1:12345", "second") == first {
		t.Fatal("不同证书指纹错误复用连接")
	}
	for key, value := range handler.cache {
		value.used = time.Now().Add(-2 * idleLifetime)
		handler.cache[key] = value
	}
	handler.connection("127.0.0.1:12345", "third")
	if len(handler.cache) != 1 {
		t.Fatal("闲置连接缓存未释放")
	}
}

func TestUntrustedHeaders(t *testing.T) {
	headers := http.Header{"X-Filesync-Bridge": {"attacker"}, "X-Filesync-Fingerprint": {"spoof"}, "Content-Type": {"application/json"}}
	stripHeaders(headers)
	if headers.Get("X-FileSync-Bridge") != "" || headers.Get("X-FileSync-Fingerprint") != "" {
		t.Fatal("伪造设备身份头未清除")
	}
	if headers.Get("Content-Type") != "application/json" {
		t.Fatal("正常请求头被改写")
	}
}

func TestCertificateLifetime(t *testing.T) {
	if validClient(tls.ConnectionState{}) == nil {
		t.Fatal("缺少设备证书被接受")
	}
	expired := &x509.Certificate{NotBefore: time.Now().Add(-time.Hour), NotAfter: time.Now().Add(-time.Minute)}
	if validClient(tls.ConnectionState{PeerCertificates: []*x509.Certificate{expired}}) == nil {
		t.Fatal("过期设备证书被接受")
	}
}
