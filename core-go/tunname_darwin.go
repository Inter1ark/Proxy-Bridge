//go:build darwin

package main

const defaultTunName = "utun"

// tokenFileGroup is the group that may read the daemon token file: "staff" (gid 20),
// every local user. The app's user is often not an administrator (the admin only
// types the password when the helper is installed).
const tokenFileGroup = 20
