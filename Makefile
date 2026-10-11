# FileMover — build, test, debug and release shortcuts.
# Requires: .NET 10 SDK, GNU make, Windows PowerShell. `make help` lists targets.

SOLUTION   := FileMover.slnx
PROJECT    := src/FileMover.csproj
CONFIG     ?= Release
RUNTIME    ?= win-x64
VERSION    ?= $(strip $(shell powershell -NoProfile -ExecutionPolicy Bypass -File scripts/version.ps1))
PUBLISH    := publish/$(RUNTIME)
DIST       := dist
PS         := powershell -NoProfile -ExecutionPolicy Bypass

.DEFAULT_GOAL := help
.PHONY: help restore build rebuild test coverage test-sftp run-desktop run-web debug-desktop debug-web watch-web \
        publish publish-arm64 publish-all package release outdated vulnerable audit format format-check clean distclean

help: ## Show this help
	@$(PS) -File scripts/help.ps1

# ── Build & test ─────────────────────────────────────────────────

restore: ## Restore NuGet packages
	dotnet restore $(SOLUTION)

build: ## Build the solution (warnings are errors)
	dotnet build $(SOLUTION) -c $(CONFIG)

rebuild: clean build ## Clean then build

test: ## Run all tests (SFTP integration tests skip without a server)
	dotnet test --solution $(SOLUTION) -c $(CONFIG)

coverage: ## Run tests with code coverage -> TestResults/coverage.cobertura.xml
	dotnet test --solution $(SOLUTION) -c $(CONFIG) --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml

test-sftp: ## Run tests including SFTP integration against a throwaway Docker OpenSSH server
	$(PS) -File scripts/test-sftp.ps1 -Configuration $(CONFIG)

# ── Run & debug ──────────────────────────────────────────────────

run-desktop: ## Run the WPF desktop UI (Release)
	dotnet run --project $(PROJECT) -c Release -- -d

run-web: ## Run the web UI on http://localhost:5050 (Release)
	dotnet run --project $(PROJECT) -c Release -- -w

debug-desktop: ## Run the desktop UI as a Debug build (attach a debugger, or use VS Code F5)
	dotnet run --project $(PROJECT) -c Debug -- -d

debug-web: ## Run the web UI as a Debug build without opening a browser
	dotnet run --project $(PROJECT) -c Debug -- -w --no-browser

watch-web: ## Run the web UI with hot reload
	dotnet watch --project $(PROJECT) run -- -w --no-browser

# ── Publish & release ────────────────────────────────────────────

publish: ## Publish single-file self-contained EXE (RUNTIME=win-x64|win-arm64)
	dotnet publish $(PROJECT) -c Release -r $(RUNTIME) --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$(VERSION) -o $(PUBLISH)

publish-arm64: ## Publish the win-arm64 EXE
	$(MAKE) publish RUNTIME=win-arm64

publish-all: ## Publish win-x64 and win-arm64
	$(MAKE) publish RUNTIME=win-x64
	$(MAKE) publish RUNTIME=win-arm64

package: test publish-all ## Test, publish both runtimes and zip them with SHA-256 checksums into dist/
	$(PS) -File scripts/package.ps1 -Version $(VERSION)

release: package ## Tag v$(VERSION) and create a GitHub release with the dist/ zips (needs gh CLI)
	git diff --quiet
	git tag -a v$(VERSION) -m "FileMover v$(VERSION)"
	git push origin v$(VERSION)
	gh release create v$(VERSION) $(DIST)/*.zip $(DIST)/SHA256SUMS.txt --title "FileMover v$(VERSION)" --generate-notes

# ── Maintenance ──────────────────────────────────────────────────

outdated: ## List NuGet packages with newer versions
	dotnet list $(SOLUTION) package --outdated

vulnerable: ## List vulnerable NuGet packages (including transitive)
	dotnet list $(SOLUTION) package --vulnerable --include-transitive

audit: outdated vulnerable ## Outdated + vulnerable package report

format: ## Apply dotnet format
	dotnet format $(SOLUTION)

format-check: ## Verify formatting without changing files
	dotnet format $(SOLUTION) --verify-no-changes

clean: ## Remove build outputs
	dotnet clean $(SOLUTION) -c Debug --nologo -v q
	dotnet clean $(SOLUTION) -c Release --nologo -v q

distclean: clean ## Remove build outputs, publish/, dist/ and test results
	$(PS) -File scripts/distclean.ps1
