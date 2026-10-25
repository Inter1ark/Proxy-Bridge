//go:build darwin || linux

package main

import (
	"fmt"
	"os"
)

// writeTokenFile stores the daemon token readable by root and tokenFileGroup only.
func writeTokenFile(path, token string) error {
	if err := os.WriteFile(path, []byte(token+"\n"), 0o644); err != nil {
		return fmt.Errorf("write --token-file: %v", err)
	}
	if err := os.Chown(path, 0, tokenFileGroup); err != nil {
		return fmt.Errorf("chown --token-file: %v", err)
	}
	return os.Chmod(path, 0o644)
}
