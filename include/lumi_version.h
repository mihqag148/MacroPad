#pragma once

/* Normal release builds inject LUMI_FIRMWARE_VERSION from the LumiPad
 * <Version> in pc-app/LumiPad.App/LumiPad.App.csproj via CMakeLists.txt.
 * This fallback keeps local/nonstandard builds compilable.
 */
#ifndef LUMI_FIRMWARE_VERSION
#define LUMI_FIRMWARE_VERSION "0.0.0-dev"
#endif
