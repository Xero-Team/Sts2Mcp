# STS2_MCP developer entry points.
#
# Configuration is never hard-coded. The build resolves the game install from, in
# order: -p:STS2GameDir on the command line, the STS2_GAME_DIR environment
# variable, then build.local.props (gitignored). The Makefile only forwards what
# it was given; when it has nothing, dotnet resolves the same sources itself.
#
#   export STS2_GAME_DIR="$HOME/.local/share/Steam/steamapps/common/Slay the Spire 2"
#   make build                          # or: make build STS2_GAME_DIR=/path/to/game
#   make help                           # every target
#
# Copy build.local.props.example to build.local.props for a durable per-machine
# path, then plain `dotnet build` works without any of this.

SHELL := /bin/sh
.DEFAULT_GOAL := help

DOTNET ?= dotnet
UV ?= uv
PYTHON ?= python3
CONFIGURATION ?= Release
OUT_DIR ?= out/STS2_MCP
PROJECT := STS2_MCP.csproj
TESTS := $(sort $(patsubst %/,%,$(dir $(wildcard tests/*/*.csproj))))

# Test projects target net9.0; run them on a newer shared runtime when that is
# all the machine has, matching the note in tests/ContractShape/README.md.
DOTNET_ROLL_FORWARD ?= Major
export DOTNET_ROLL_FORWARD

# Make's copy of the game path. Empty means "let MSBuild resolve it" (from
# build.local.props); a value here is exported so every dotnet/uv call sees the
# same path, and dotnet format - which has no -p: passthrough - can find it.
GAME_DIR ?= $(STS2_GAME_DIR)
ifneq ($(strip $(GAME_DIR)),)
export STS2_GAME_DIR := $(GAME_DIR)
endif

.PHONY: help config build debug test format format-check verify audit audit-live \
        test-mcp mcp install record-selftest clean

help: ## Show this help.
	@printf 'STS2_MCP targets\n\n'
	@awk 'BEGIN {FS = ":.*?## "} /^[a-zA-Z0-9_-]+:.*?## / {printf "  %-16s %s\n", $$1, $$2}' $(MAKEFILE_LIST)
	@printf '\nGame path: export STS2_GAME_DIR=<path>, cp build.local.props.example build.local.props,\n'
	@printf 'or pass it per command: make build STS2_GAME_DIR=<path>. `make config` shows what resolves.\n'

config: ## Print the resolved game/data directories and check the DLLs exist.
	@game="$$($(DOTNET) msbuild $(PROJECT) -nologo -getProperty:STS2GameDir 2>/dev/null)"; \
	data="$$($(DOTNET) msbuild $(PROJECT) -nologo -getProperty:STS2GameDataDir 2>/dev/null)"; \
	if [ -n "$$game" ]; then printf 'STS2GameDir     = %s\n' "$$game"; \
	else printf 'STS2GameDir     = <unset> (export STS2_GAME_DIR or create build.local.props)\n'; fi; \
	if [ -n "$$data" ]; then printf 'STS2GameDataDir = %s\n' "$$data"; \
	else printf 'STS2GameDataDir = <unset>\n'; fi; \
	if [ -f "$$data/sts2.dll" ]; then printf 'sts2.dll        = found\n'; \
	else printf 'sts2.dll        = NOT FOUND\n'; fi

build: ## Build Release into out/STS2_MCP/ (installable DLL + manifest).
	$(DOTNET) build $(PROJECT) -c $(CONFIGURATION) -o $(OUT_DIR)

debug: CONFIGURATION := Debug
debug: OUT_DIR := out/STS2_MCP-debug
debug: build ## Build Debug into out/STS2_MCP-debug/.

test: ## Run every headless harness under tests/ (no game install needed).
	@set -e; for project in $(TESTS); do \
		printf '\n== %s\n' "$$project"; \
		$(DOTNET) run --project "$$project" -c $(CONFIGURATION); \
	done

format: ## Apply dotnet format (whitespace, imports, style) to the mod project.
	$(DOTNET) format $(PROJECT)

format-check: ## Verify formatting without changing files.
	$(DOTNET) format $(PROJECT) --verify-no-changes

verify: build format-check test ## Local gate: build, formatting, headless tests.

audit: ## Static endpoint/doc audit (no game needed).
	$(PYTHON) scripts/audit_endpoints.py --skip-live

audit-live: ## Full endpoint audit against a running game.
	$(PYTHON) scripts/audit_endpoints.py

test-mcp: ## Run the Python MCP bridge tests (needs uv).
	$(UV) run --project mcp python scripts/test_mcp_server.py

mcp: ## Run the MCP server against a running game (needs uv).
	$(UV) run --directory mcp python server.py

install: build ## Copy the built mod into <game>/mods/ (MODS_DIR overrides the path).
	@game="$(GAME_DIR)"; \
	if [ -z "$$game" ]; then game="$$($(DOTNET) msbuild $(PROJECT) -nologo -getProperty:STS2GameDir 2>/dev/null)"; fi; \
	if [ -z "$$game" ]; then \
		echo "error: game path unresolved; set STS2_GAME_DIR or create build.local.props" >&2; \
		exit 1; \
	fi; \
	mods="$(MODS_DIR)"; \
	if [ -z "$$mods" ]; then \
		case "$$(uname -s)" in \
			Darwin) mods="$$game/SlayTheSpire2.app/Contents/MacOS/mods" ;; \
			*) mods="$$game/mods" ;; \
		esac; \
	fi; \
	install -d "$$mods"; \
	install -m 644 "$(OUT_DIR)/STS2_MCP.dll" "$$mods/STS2_MCP.dll"; \
	install -m 644 "$(OUT_DIR)/STS2_MCP.json" "$$mods/STS2_MCP.json"; \
	printf 'Installed into %s\n' "$$mods"

record-selftest: ## Run the record-check regression cases (no game needed).
	$(PYTHON) tools/record_check_selftest.py

clean: ## Remove build output (bin/, obj/, out/).
	rm -rf bin obj out
	rm -rf tests/*/bin tests/*/obj
