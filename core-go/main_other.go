//go:build !darwin && !linux

package main

import (
	"fmt"
	"os"
)

func main() {
	fmt.Fprintln(os.Stderr, "pbcore: this binary only runs on macOS and Linux")
	os.Exit(2)
}
