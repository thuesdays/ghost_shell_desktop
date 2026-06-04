// Copyright 2026 Ghost Shell Browser.

#include "components/embedder_support/ghost_shell_ua_override.h"

#include "base/base64.h"
#include "base/command_line.h"
#include "base/json/json_reader.h"
#include "base/logging.h"
#include "base/no_destructor.h"
#include "base/strings/string_util.h"  // audit PCP-03: base::ToLowerASCII
#include "base/values.h"

namespace embedder_support {

namespace {

std::string StringOrEmpty(const std::string* s) {
  return s ? *s : std::string();
}

// Parses a list of {"brand": "X", "version": "Y"} dicts into a vector
// of UserAgentBrandVersion.
std::vector<blink::UserAgentBrandVersion> ParseBrandList(
    const base::ListValue& list) {
  std::vector<blink::UserAgentBrandVersion> out;
  out.reserve(list.size());
  for (const auto& v : list) {
    if (!v.is_dict()) continue;
    const auto& d = v.GetDict();
    const std::string* brand = d.FindString("brand");
    const std::string* ver   = d.FindString("version");
    if (brand && ver) {
      blink::UserAgentBrandVersion bv;
      bv.brand   = *brand;
      bv.version = *ver;
      out.push_back(std::move(bv));
    }
  }
  return out;
}

}  // namespace

// Singleton instance — NoDestructor to avoid exit-time ordering issues.
GhostShellUAOverride& GhostShellUAOverride::GetInstance() {
  static base::NoDestructor<GhostShellUAOverride> instance;
  return *instance;
}

GhostShellUAOverride::GhostShellUAOverride() {
  Initialize();
}

GhostShellUAOverride::~GhostShellUAOverride() = default;

void GhostShellUAOverride::Initialize() {
  base::CommandLine* cmd = base::CommandLine::ForCurrentProcess();
  if (!cmd->HasSwitch("ghost-shell-payload")) {
    is_active_ = false;
    return;
  }

  std::string b64 = cmd->GetSwitchValueASCII("ghost-shell-payload");
  std::string json_str;
  if (!base::Base64Decode(b64, &json_str)) {
    LOG(ERROR) << "[GhostShellUA] base64 decode failed";
    is_active_ = false;
    return;
  }

  auto parsed = base::JSONReader::Read(json_str, 0);
  if (!parsed || !parsed->is_dict()) {
    LOG(ERROR) << "[GhostShellUA] JSON parse failed";
    is_active_ = false;
    return;
  }

  const auto& root = parsed->GetDict();
  const auto* uam = root.FindDict("ua_metadata");
  if (!uam) {
    // No ua_metadata block — caller shouldn't spoof.
    is_active_ = false;
    return;
  }

  full_version_      = StringOrEmpty(uam->FindString("full_version"));
  platform_          = StringOrEmpty(uam->FindString("platform"));
  platform_version_  = StringOrEmpty(uam->FindString("platform_version"));
  architecture_      = StringOrEmpty(uam->FindString("architecture"));
  bitness_           = StringOrEmpty(uam->FindString("bitness"));
  model_             = StringOrEmpty(uam->FindString("model"));
  mobile_            = uam->FindBool("mobile").value_or(false);
  wow64_             = uam->FindBool("wow64").value_or(false);

  if (const auto* brands = uam->FindList("brands")) {
    brand_version_list_ = ParseBrandList(*brands);
  }
  if (const auto* full_brands = uam->FindList("full_version_list")) {
    brand_full_version_list_ = ParseBrandList(*full_brands);
  }

  // audit PCP-03: parse ua_metadata.form_factor (singular string) and map it
  // to the plural blink form-factor vector. Previously this key had NO consumer
  // and GhostBrowserConfig hardcoded {"Desktop"} regardless of the payload, so
  // a mobile/tablet profile reported mobile=true with form factor "Desktop" —
  // an internal contradiction CreepJS/FingerprintJS flag. Mapping it here, in
  // the single consolidated UA parser, makes Sec-CH-UA-Form-Factors coherent
  // with the mobile bit and the platform.
  if (const std::string* ff = uam->FindString("form_factor")) {
    const std::string ff_lower = base::ToLowerASCII(*ff);
    if (ff_lower == "mobile") {
      form_factors_ = {blink::kMobileFormFactor};
    } else if (ff_lower == "tablet") {
      form_factors_ = {blink::kTabletFormFactor};
    } else if (ff_lower == "xr") {
      form_factors_ = {blink::kXRFormFactor};
    } else {
      form_factors_ = {blink::kDesktopFormFactor};
    }
  } else {
    // No explicit form_factor — derive from the mobile bit so it can never
    // contradict it (mobile→Mobile, else Desktop), matching native default
    // behaviour in GetFormFactorsClientHint().
    form_factors_ = {mobile_ ? blink::kMobileFormFactor
                             : blink::kDesktopFormFactor};
  }

  // audit PCP-06: the q-weighted Accept-Language string lives under the
  // top-level "languages" block, not ua_metadata. Cache it so the network
  // layer can enforce the exact header (matching navigator.languages order).
  if (const auto* langs = root.FindDict("languages")) {
    accept_language_ = StringOrEmpty(langs->FindString("accept_language"));
  }

  is_active_ = true;
  VLOG(1) << "[GhostShellUA] loaded, platform=" << platform_
          << " ver=" << full_version_;
}

}  // namespace embedder_support
