#include "Pack.h"
#include <godot_cpp/classes/global_constants.hpp>
#include <godot_cpp/core/class_db.hpp>

void Pack::_bind_methods() {
	ClassDB::bind_method(D_METHOD("get_pack_name"), &Pack::get_pack_name);
	ClassDB::bind_method(D_METHOD("set_pack_name", "p_name"), &Pack::set_pack_name);

	ADD_PROPERTY(PropertyInfo(Variant::STRING_NAME, "pack_name", godot::PROPERTY_HINT_TYPE_STRING, "pack_name", godot::PROPERTY_USAGE_DEFAULT), "set_pack_name", "get_pack_name");
}
