SHELL := /usr/bin/env bash
.SHELLFLAGS := -euo pipefail -c

# ---- Project paths ----
SOLUTION := SteamDl.sln
SERVER_PROJECT := src/SteamDl.Server/SteamDl.Server.csproj
ANDROID_PROJECT := src/SteamDl.Android/SteamDl.Android.csproj

# ---- Build options ----
CONFIG ?= Release
PORT ?= 8630
RUNTIME ?= linux-x64
ANDROID_API ?= 35
ANDROID_BUILD_TOOLS ?= 35.0.0
APK_OUT ?= artifacts/apk
SERVER_OUT ?= artifacts/server

# ---- Local toolchain (fallback if system tools are missing) ----
TOOLS_DIR := $(CURDIR)/.tools
LOCAL_DOTNET_DIR := $(TOOLS_DIR)/dotnet
LOCAL_DOTNET := $(LOCAL_DOTNET_DIR)/dotnet
LOCAL_JDK_DIR := $(TOOLS_DIR)/jdk
ANDROID_SDK_ROOT ?= $(CURDIR)/.tools/android-sdk
SDKMANAGER ?= $(ANDROID_SDK_ROOT)/cmdline-tools/latest/bin/sdkmanager

DOTNET ?= $(shell command -v dotnet 2>/dev/null || printf '%s' '$(LOCAL_DOTNET)')
DOTNET_CHANNEL ?= 9.0
DOTNET_ENV := DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 NUGET_PACKAGES=$(TOOLS_DIR)/nuget HOME=$(TOOLS_DIR)/home DOTNET_CLI_HOME=$(TOOLS_DIR)/home
ANDROID_ENV := ANDROID_HOME=$(ANDROID_SDK_ROOT) ANDROID_SDK_ROOT=$(ANDROID_SDK_ROOT)
CMDLINE_TOOLS_ZIP := $(TOOLS_DIR)/commandlinetools-linux.zip
CMDLINE_TOOLS_URL ?= https://dl.google.com/android/repository/commandlinetools-linux-11076708_latest.zip
JDK_TARBALL := $(TOOLS_DIR)/jdk17-linux-x64.tar.gz
JDK_URL ?= https://api.adoptium.net/v3/binary/latest/17/ga/linux/x64/jdk/hotspot/normal/eclipse

.PHONY: help doctor install-deps install-dotnet install-jdk install-android-sdk-tools install-android-sdk install-workload restore restore-server restore-android build build-server run publish-server build-apk publish-apk clean clean-artifacts

help:
	@echo "SteamDl build targets"
	@echo ""
	@echo "  make doctor              检查 dotnet/java/Android SDK 环境"
	@echo "  make install-deps        本地安装依赖(.NET/JDK/Android SDK/workload/restore)"
	@echo "  make restore             还原桌面服务端 NuGet 包"
	@echo "  make build               构建桌面服务端"
	@echo "  make run                 运行桌面服务端(http://127.0.0.1:$(PORT))"
	@echo "  make publish-server      发布桌面服务端到 $(SERVER_OUT)"
	@echo "  make build-apk           构建 Android APK 到 $(APK_OUT)"
	@echo "  make clean               清理 bin/obj/artifacts"
	@echo ""
	@echo "可覆盖变量: CONFIG=Release PORT=8630 ANDROID_API=35 ANDROID_BUILD_TOOLS=35.0.0"

doctor:
	@echo "== System =="
	@uname -a || true
	@echo "CPU: $$(nproc 2>/dev/null || sysctl -n hw.ncpu 2>/dev/null || echo unknown)"
	@free -h 2>/dev/null || true
	@df -h . 2>/dev/null || true
	@echo ""
	@echo "== .NET =="
	@if [ -x "$(DOTNET)" ]; then $(DOTNET_ENV) "$(DOTNET)" --info; else echo "dotnet 未找到: 运行 make install-dotnet"; fi
	@echo ""
	@echo "== Java =="
	@if [ -x "$(LOCAL_JDK_DIR)/bin/java" ]; then "$(LOCAL_JDK_DIR)/bin/java" -version; elif command -v java >/dev/null 2>&1; then java -version; else echo "java 未找到: 运行 make install-jdk"; fi
	@echo ""
	@echo "== Android SDK =="
	@echo "ANDROID_SDK_ROOT=$(ANDROID_SDK_ROOT)"
	@if [ -x "$(SDKMANAGER)" ]; then "$(SDKMANAGER)" --version; elif command -v sdkmanager >/dev/null 2>&1; then sdkmanager --version; else echo "sdkmanager 未找到: 运行 make install-android-sdk-tools"; fi

install-deps: install-dotnet install-jdk install-android-sdk install-workload restore-android
	@echo "依赖安装完成。可执行: make build 或 make build-apk"

install-dotnet:
	@mkdir -p "$(TOOLS_DIR)" "$(TOOLS_DIR)/home" "$(TOOLS_DIR)/tmp"
	@if command -v dotnet >/dev/null 2>&1; then \
		echo "使用系统 dotnet: $$(command -v dotnet)"; $(DOTNET_ENV) dotnet --version; \
	elif [ -x "$(LOCAL_DOTNET)" ]; then \
		echo "使用本地 dotnet: $(LOCAL_DOTNET)"; $(DOTNET_ENV) "$(LOCAL_DOTNET)" --version; \
	else \
		echo "安装 .NET SDK $(DOTNET_CHANNEL) 到 $(LOCAL_DOTNET_DIR)"; \
		curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$(TOOLS_DIR)/dotnet-install.sh"; \
		TMPDIR="$(TOOLS_DIR)/tmp" bash "$(TOOLS_DIR)/dotnet-install.sh" --channel "$(DOTNET_CHANNEL)" --install-dir "$(LOCAL_DOTNET_DIR)"; \
		$(DOTNET_ENV) "$(LOCAL_DOTNET)" --version; \
	fi

install-jdk:
	@mkdir -p "$(TOOLS_DIR)"
	@if command -v java >/dev/null 2>&1; then \
		echo "使用系统 Java: $$(command -v java)"; java -version; \
	elif [ -x "$(LOCAL_JDK_DIR)/bin/java" ]; then \
		echo "使用本地 JDK: $(LOCAL_JDK_DIR)"; "$(LOCAL_JDK_DIR)/bin/java" -version; \
	else \
		if [ "$$(uname -s)" != "Linux" ] || [ "$$(uname -m)" != "x86_64" ]; then \
			echo "自动安装 JDK 仅支持 Linux x86_64；请手动安装 JDK 17 后重试。"; exit 1; \
		fi; \
		echo "安装 JDK 17 到 $(LOCAL_JDK_DIR)"; \
		curl -fL "$(JDK_URL)" -o "$(JDK_TARBALL)"; \
		rm -rf "$(LOCAL_JDK_DIR)" "$(TOOLS_DIR)/jdk-extract"; \
		mkdir -p "$(TOOLS_DIR)/jdk-extract"; \
		tar -xzf "$(JDK_TARBALL)" -C "$(TOOLS_DIR)/jdk-extract" --strip-components=1; \
		mv "$(TOOLS_DIR)/jdk-extract" "$(LOCAL_JDK_DIR)"; \
		"$(LOCAL_JDK_DIR)/bin/java" -version; \
	fi

install-android-sdk-tools: install-jdk
	@mkdir -p "$(ANDROID_SDK_ROOT)/cmdline-tools" "$(TOOLS_DIR)"
	@if [ -x "$(SDKMANAGER)" ]; then \
		echo "sdkmanager 已存在: $(SDKMANAGER)"; \
	else \
		command -v unzip >/dev/null 2>&1 || { echo "缺少 unzip，请先安装 unzip"; exit 1; }; \
		echo "安装 Android cmdline-tools 到 $(ANDROID_SDK_ROOT)"; \
		curl -fL "$(CMDLINE_TOOLS_URL)" -o "$(CMDLINE_TOOLS_ZIP)"; \
		rm -rf "$(ANDROID_SDK_ROOT)/cmdline-tools/latest" "$(TOOLS_DIR)/cmdline-tools-tmp"; \
		mkdir -p "$(TOOLS_DIR)/cmdline-tools-tmp"; \
		unzip -q "$(CMDLINE_TOOLS_ZIP)" -d "$(TOOLS_DIR)/cmdline-tools-tmp"; \
		mv "$(TOOLS_DIR)/cmdline-tools-tmp/cmdline-tools" "$(ANDROID_SDK_ROOT)/cmdline-tools/latest"; \
	fi
	@if [ -x "$(LOCAL_JDK_DIR)/bin/java" ]; then export JAVA_HOME="$(LOCAL_JDK_DIR)" PATH="$(LOCAL_JDK_DIR)/bin:$$PATH"; fi; "$(SDKMANAGER)" --version

install-android-sdk: install-android-sdk-tools
	@echo "安装 Android SDK platform/build-tools"
	@if [ -x "$(LOCAL_JDK_DIR)/bin/java" ]; then export JAVA_HOME="$(LOCAL_JDK_DIR)" PATH="$(LOCAL_JDK_DIR)/bin:$$PATH"; fi; "$(SDKMANAGER)" --sdk_root="$(ANDROID_SDK_ROOT)" --licenses >/dev/null || true
	@if [ -x "$(LOCAL_JDK_DIR)/bin/java" ]; then export JAVA_HOME="$(LOCAL_JDK_DIR)" PATH="$(LOCAL_JDK_DIR)/bin:$$PATH"; fi; "$(SDKMANAGER)" --sdk_root="$(ANDROID_SDK_ROOT)" \
		"platform-tools" \
		"platforms;android-$(ANDROID_API)" \
		"build-tools;$(ANDROID_BUILD_TOOLS)" \
		"cmdline-tools;latest"

install-workload: install-dotnet
	@echo "安装/还原 .NET Android workload"
	@$(DOTNET_ENV) "$(DOTNET)" workload restore "$(ANDROID_PROJECT)"

restore: restore-server

restore-server: install-dotnet
	@$(DOTNET_ENV) "$(DOTNET)" restore "$(SERVER_PROJECT)"

restore-android: install-workload install-android-sdk
	@if [ -x "$(LOCAL_JDK_DIR)/bin/java" ]; then export JAVA_HOME="$(LOCAL_JDK_DIR)" PATH="$(LOCAL_JDK_DIR)/bin:$$PATH"; fi; \
		$(ANDROID_ENV) $(DOTNET_ENV) "$(DOTNET)" restore "$(ANDROID_PROJECT)"

build: build-server

build-server: restore
	@$(DOTNET_ENV) "$(DOTNET)" build "$(SERVER_PROJECT)" -c "$(CONFIG)" --no-restore

run: restore
	@echo "启动 http://127.0.0.1:$(PORT)"
	@PORT="$(PORT)" $(DOTNET_ENV) "$(DOTNET)" run --project "$(SERVER_PROJECT)" -c "$(CONFIG)" --no-restore

publish-server: restore
	@rm -rf "$(SERVER_OUT)"
	@$(DOTNET_ENV) "$(DOTNET)" publish "$(SERVER_PROJECT)" -c "$(CONFIG)" -r "$(RUNTIME)" --self-contained false -o "$(SERVER_OUT)"
	@echo "桌面服务端产物: $(SERVER_OUT)"

build-apk publish-apk: install-deps
	@rm -rf "$(APK_OUT)"
	@mkdir -p "$(APK_OUT)"
	@if [ -x "$(LOCAL_JDK_DIR)/bin/java" ]; then export JAVA_HOME="$(LOCAL_JDK_DIR)" PATH="$(LOCAL_JDK_DIR)/bin:$(ANDROID_SDK_ROOT)/platform-tools:$$PATH"; fi; \
		$(ANDROID_ENV) $(DOTNET_ENV) "$(DOTNET)" publish "$(ANDROID_PROJECT)" -c "$(CONFIG)" \
		-p:AndroidPackageFormat=apk
	@find src/SteamDl.Android/bin/$(CONFIG) -name "*.apk" -type f -exec cp {} "$(APK_OUT)/" \;
	@echo "APK 产物目录: $(APK_OUT)"
	@find "$(APK_OUT)" -name "*.apk" -type f -maxdepth 1 -print

clean:
	@find src -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
	@rm -rf artifacts
	@echo "已清理构建产物"

clean-artifacts:
	@rm -rf artifacts
	@echo "已清理 artifacts"
