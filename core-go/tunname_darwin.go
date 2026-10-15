//go:build darwin

package main

const defaultTunName = "utun"

// tokenFileGroup is the group that may read the daemon token file: "admin"
// (gid 80), so only administrators of the Mac can drive the daemon.
const tokenFileGroup = 80
