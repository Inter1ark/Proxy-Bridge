//go:build darwin || linux

package tunnel

// Logger is what the tunnel reports to: the pbcore process logger on desktop,
// the host application's log on mobile.
type Logger interface {
	Debugf(format string, args ...any)
	Infof(format string, args ...any)
	Warnf(format string, args ...any)
	Errorf(format string, args ...any)
}
