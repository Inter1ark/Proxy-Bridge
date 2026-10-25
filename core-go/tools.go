//go:build tools

package main

// Keeps the gomobile bind dependency in go.mod (go mod tidy would drop it otherwise).
import _ "golang.org/x/mobile/bind"
