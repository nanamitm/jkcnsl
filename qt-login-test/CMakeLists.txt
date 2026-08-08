cmake_minimum_required(VERSION 3.21)

project(jkcnsl_qt_login_test VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_AUTOMOC ON)
set(CMAKE_AUTORCC ON)

find_package(Qt6 6.8 REQUIRED COMPONENTS Network Widgets WebEngineWidgets)

qt_add_executable(jkcnsl-qt-login
    main.cpp
    assets/jkcnsl-login.rc
    assets/jkcnsl-login.qrc
)

target_link_libraries(jkcnsl-qt-login PRIVATE
    Qt6::Network
    Qt6::Widgets
    Qt6::WebEngineWidgets
)

if(WIN32)
    target_link_libraries(jkcnsl-qt-login PRIVATE Crypt32)
endif()
