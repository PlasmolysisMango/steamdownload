SHELL := /usr/bin/env bash
.SHELLFLAGS := -euo pipefail -c

# Makefile 只作为 build.mjs 的 Linux/macOS/WSL 转发入口，不承载独立构建逻辑。
NODE ?= node
BUILD := $(NODE) build.mjs

CONFIG ?= Release
PORT ?= 8630
RUNTIME ?= linux-x64
ANDROID_API ?= 35
ANDROID_BUILD_TOOLS ?= 35.0.0
DOTNET_CHANNEL ?= 9.0
JDK_VERSION ?= 17.0.19_10
JDK_URL ?=
ANDROID_CMDLINE_TOOLS_URL ?=
NUGET_SOURCE ?=
NPM_REGISTRY ?= https://registry.npmmirror.com
WEB_DOCKER_IMAGE ?= docker.m.daocloud.io/library/node:22-bookworm
DOTNET_DOCKER_IMAGE ?= m.daocloud.io/mcr.microsoft.com/dotnet/sdk:9.0

BASE_ARGS := --port=$(PORT) --runtime=$(RUNTIME) --android-api=$(ANDROID_API) --android-build-tools=$(ANDROID_BUILD_TOOLS) --dotnet-channel=$(DOTNET_CHANNEL) --jdk-version=$(JDK_VERSION) --jdk-url=$(JDK_URL) --android-cmdline-tools-url=$(ANDROID_CMDLINE_TOOLS_URL) --nuget-source=$(NUGET_SOURCE) --npm-registry=$(NPM_REGISTRY) --web-docker-image=$(WEB_DOCKER_IMAGE) --dotnet-docker-image=$(DOTNET_DOCKER_IMAGE)
COMMON_ARGS := --config=$(CONFIG) $(BASE_ARGS)

.PHONY: help doctor install-deps install-dotnet install-jdk install-android-sdk install-workload restore build-web docker-build-web build build-server docker-build docker-build-server run publish-server docker-publish-server build-apk publish-apk docker-build-apk docker-publish-apk docker-debug-apk docker-release-apk clean clean-artifacts

help:
	@$(BUILD) help $(COMMON_ARGS)

doctor:
	@$(BUILD) doctor $(COMMON_ARGS)

install-deps:
	@$(BUILD) install-deps $(COMMON_ARGS)

install-dotnet:
	@$(BUILD) install-dotnet $(COMMON_ARGS)

install-jdk:
	@$(BUILD) install-jdk $(COMMON_ARGS)

install-android-sdk:
	@$(BUILD) install-android-sdk $(COMMON_ARGS)

install-workload:
	@$(BUILD) install-workload $(COMMON_ARGS)

restore:
	@$(BUILD) restore $(COMMON_ARGS)

build-web:
	@$(BUILD) build-web $(COMMON_ARGS)

docker-build-web:
	@$(BUILD) docker-build-web $(COMMON_ARGS)

build build-server:
	@$(BUILD) build $(COMMON_ARGS)

docker-build docker-build-server:
	@$(BUILD) docker-build $(COMMON_ARGS)

run:
	@$(BUILD) run $(COMMON_ARGS)

publish-server:
	@$(BUILD) publish-server $(COMMON_ARGS)

docker-publish-server:
	@$(BUILD) docker-publish-server $(COMMON_ARGS)

build-apk publish-apk:
	@$(BUILD) build-apk $(COMMON_ARGS)

docker-build-apk docker-publish-apk:
	@$(BUILD) docker-build-apk $(COMMON_ARGS)

docker-release-apk:
	@$(BUILD) docker-build-apk --config=Release $(BASE_ARGS)

docker-debug-apk:
	@$(BUILD) docker-build-apk --config=Debug $(BASE_ARGS)

clean:
	@$(BUILD) clean $(COMMON_ARGS)

clean-artifacts:
	@$(BUILD) clean-artifacts $(COMMON_ARGS)
