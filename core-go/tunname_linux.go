//go:build linux

package main

const defaultTunName = "pbtun0"

// tokenFileGroup is the group that may read the daemon token file (root on Linux).
const tokenFileGroup = 0
