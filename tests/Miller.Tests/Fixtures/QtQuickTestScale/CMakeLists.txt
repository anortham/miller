cmake_minimum_required(VERSION 3.21)
project(miller_qml_scale LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

enable_testing()

find_package(Qt6 REQUIRED COMPONENTS QuickTest Qml)

add_definitions(-DQUICK_TEST_SOURCE_DIR="${CMAKE_CURRENT_SOURCE_DIR}")

add_executable(miller_qml_quick_tests runner.cpp)
target_link_libraries(miller_qml_quick_tests PRIVATE Qt6::QuickTest Qt6::Qml)

add_test(
    NAME qml/basic
    COMMAND miller_qml_quick_tests -input "${CMAKE_CURRENT_SOURCE_DIR}/tst_basic.qml")
add_test(
    NAME qml/second
    COMMAND miller_qml_quick_tests -input "${CMAKE_CURRENT_SOURCE_DIR}/tst_second.qml")
