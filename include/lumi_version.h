#pragma once

/* Release builds inject LUMI_FIRMWARE_VERSION from the repository VERSION file.
 * This fallback keeps local/nonstandard builds compilable.
 */
#ifndef LUMI_FIRMWARE_VERSION
#define LUMI_FIRMWARE_VERSION "0.0.0-dev"
#endif
