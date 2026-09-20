if(CONFIG_ZMK_DISPLAY_STATUS_SCREEN_CUSTOM)
  # Single source of truth for release version:
  # pc-app/LumiPad.App/LumiPad.App.csproj <Version>.
  # Firmware HELLO automatically reports the exact same version.
  file(READ
    "${CMAKE_CURRENT_LIST_DIR}/pc-app/LumiPad.App/LumiPad.App.csproj"
    LUMI_APP_CSPROJ
  )
  string(
    REGEX MATCH
    "<Version>([0-9]+\.[0-9]+\.[0-9]+)</Version>"
    LUMI_VERSION_MATCH
    "${LUMI_APP_CSPROJ}"
  )
  if(NOT CMAKE_MATCH_1)
    message(FATAL_ERROR "Unable to read LumiPad <Version> from csproj")
  endif()
  set(LUMI_RELEASE_VERSION "${CMAKE_MATCH_1}")
  message(STATUS "Lumi release version: ${LUMI_RELEASE_VERSION}")

  target_include_directories(app PRIVATE
    ${CMAKE_CURRENT_LIST_DIR}/include
  )
  target_compile_definitions(
    app PRIVATE
    LUMI_FIRMWARE_VERSION=\"${LUMI_RELEASE_VERSION}\"
  )

  target_sources(app PRIVATE
    ${CMAKE_CURRENT_LIST_DIR}/src/lumi_panel.c
    ${CMAKE_CURRENT_LIST_DIR}/src/lumi_status_screen.c
    ${CMAKE_CURRENT_LIST_DIR}/src/lumi_rgb.c
    ${CMAKE_CURRENT_LIST_DIR}/src/lumi_action.c
    ${CMAKE_CURRENT_LIST_DIR}/src/lumi_now_playing.c
    ${CMAKE_CURRENT_LIST_DIR}/src/lumi_app_link.c
  )
endif()
