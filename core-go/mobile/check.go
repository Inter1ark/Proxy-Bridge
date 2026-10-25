//go:build linux

package pbmobile

import "proxybridge/core/internal/check"

// CheckProxy connects through the proxy to ip-api.com (same check as on the desktop) and,
// for SOCKS5, asks for UDP ASSOCIATE and sends a STUN request through it. lang is "ru" or
// "en". The result is JSON: {ok, error, ip, country, country_code, city, ms, udp, udp_ip}.
func CheckProxy(rawURL, lang string) string {
	return check.JSON(rawURL, lang)
}
