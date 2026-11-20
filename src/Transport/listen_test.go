package main

import "testing"

func TestLoopbackTestBinding(t *testing.T) {
	if host := listenHost(configuration{LoopbackOnly: true}); host != "127.0.0.1" {
		t.Fatalf("isolated tests must only listen on loopback, got %s", host)
	}
	if host := listenHost(configuration{}); host != "0.0.0.0" {
		t.Fatalf("normal LAN mode must retain its explicit network listener, got %s", host)
	}
}
