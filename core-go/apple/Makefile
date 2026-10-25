# Builds libpbcore.a for iOS (and macOS) from core-go/apple with cgo, the way wireguard-apple
# builds its Go bridge. Xcode passes the variables; defaults suit a standalone run:
#
#   make PLATFORM_NAME=iphoneos ARCHS=arm64 IPHONEOS_DEPLOYMENT_TARGET=16.0 \
#        DEPLOYMENT_TARGET_CLANG_FLAG_NAME=miphoneos-version-min \
#        DEPLOYMENT_TARGET_CLANG_ENV_NAME=IPHONEOS_DEPLOYMENT_TARGET DESTDIR=$PWD/out
ARCHS ?= arm64
PLATFORM_NAME ?= iphoneos
SDKROOT ?= $(shell xcrun --sdk $(PLATFORM_NAME) --show-sdk-path)
CONFIGURATION_BUILD_DIR ?= $(CURDIR)/out
CONFIGURATION_TEMP_DIR ?= $(CURDIR)/.tmp
VERSION ?= dev

export PATH := $(PATH):/usr/local/bin:/opt/homebrew/bin
export CC ?= clang
LIPO ?= lipo
DESTDIR ?= $(CONFIGURATION_BUILD_DIR)
BUILDDIR ?= $(CONFIGURATION_TEMP_DIR)/pbcore-bridge

CFLAGS_PREFIX := $(if $(DEPLOYMENT_TARGET_CLANG_FLAG_NAME),-$(DEPLOYMENT_TARGET_CLANG_FLAG_NAME)=$($(DEPLOYMENT_TARGET_CLANG_ENV_NAME)),) -isysroot $(SDKROOT) -arch
GOARCH_arm64 := arm64
GOARCH_x86_64 := amd64
GOOS_macosx := darwin
GOOS_iphoneos := ios
GOOS_iphonesimulator := ios

build: $(DESTDIR)/libpbcore.a

REAL_GOROOT := $(shell go env GOROOT 2>/dev/null)
export GOROOT := $(BUILDDIR)/goroot
$(GOROOT)/.prepared:
	[ -n "$(REAL_GOROOT)" ]
	mkdir -p "$(GOROOT)"
	rsync -a --delete --exclude=pkg/obj/go-build "$(REAL_GOROOT)/" "$(GOROOT)/"
	cat goruntime-*.diff | patch -p1 -f -N -r- -d "$(GOROOT)" || echo "goruntime patch skipped"
	touch "$@"

define libpbcore-a
$(BUILDDIR)/libpbcore-$(1).a: export CGO_ENABLED := 1
$(BUILDDIR)/libpbcore-$(1).a: export CGO_CFLAGS := $(CFLAGS_PREFIX) $(ARCH)
$(BUILDDIR)/libpbcore-$(1).a: export CGO_LDFLAGS := $(CFLAGS_PREFIX) $(ARCH)
$(BUILDDIR)/libpbcore-$(1).a: export GOOS := $(GOOS_$(PLATFORM_NAME))
$(BUILDDIR)/libpbcore-$(1).a: export GOARCH := $(GOARCH_$(1))
$(BUILDDIR)/libpbcore-$(1).a: $(GOROOT)/.prepared ../go.mod
	go build -tags netcgo -ldflags="-w -X main.version=$(VERSION)" -trimpath -v -o "$(BUILDDIR)/libpbcore-$(1).a" -buildmode c-archive .
	rm -f "$(BUILDDIR)/libpbcore-$(1).h"
endef
$(foreach ARCH,$(ARCHS),$(eval $(call libpbcore-a,$(ARCH))))

$(DESTDIR)/libpbcore.a: $(foreach ARCH,$(ARCHS),$(BUILDDIR)/libpbcore-$(ARCH).a)
	@mkdir -vp "$(DESTDIR)"
	$(LIPO) -create -output "$@" $^

clean:
	rm -rf "$(BUILDDIR)" "$(DESTDIR)/libpbcore.a"

.PHONY: clean build
