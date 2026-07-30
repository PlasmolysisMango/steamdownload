SHELL := /usr/bin/env bash
.SHELLFLAGS := -euo pipefail -c

# Makefile 只作为 build.mjs 的转发入口，不承载独立构建逻辑。
# 正式构建(APK/桌面包)一律通过 GitHub Actions 完成。
NODE ?= node
BUILD := $(NODE) build.mjs

CONFIG ?= Release
PORT ?= 8630
RUNTIME ?= linux-x64

COMMON_ARGS := --config=$(CONFIG) --port=$(PORT) --runtime=$(RUNTIME)

.PHONY: help doctor run publish-engine flutter-run clean

help:
	@$(BUILD) help $(COMMON_ARGS)

doctor:
	@$(BUILD) doctor $(COMMON_ARGS)

run:
	@$(BUILD) run $(COMMON_ARGS)

publish-engine:
	@$(BUILD) publish-engine $(COMMON_ARGS)

flutter-run:
	@$(BUILD) flutter-run $(COMMON_ARGS)

clean:
	@$(BUILD) clean $(COMMON_ARGS)
