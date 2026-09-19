if(CONFIG_ZMK_DISPLAY_STATUS_SCREEN_CUSTOM)
  target_sources(app PRIVATE
    ${CMAKE_CURRENT_LIST_DIR}/src/lumi_panel.c
    ${CMAKE_CURRENT_LIST_DIR}/src/lumi_status_screen.c
    ${CMAKE_CURRENT_LIST_DIR}/src/lumi_rgb.c
    ${CMAKE_CURRENT_LIST_DIR}/src/lumi_action.c
    ${CMAKE_CURRENT_LIST_DIR}/src/lumi_now_playing.c
    ${CMAKE_CURRENT_LIST_DIR}/src/lumi_app_link.c
  )
endif()
