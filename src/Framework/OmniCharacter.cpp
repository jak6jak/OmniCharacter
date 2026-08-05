#include "OmniCharacter.h"
#include <godot_cpp/classes/global_constants.hpp>
#include <godot_cpp/core/class_db.hpp>
#include <godot_cpp/core/object.hpp>
#include <godot_cpp/core/property_info.hpp>
#include <godot_cpp/variant/array.hpp>
#include <godot_cpp/variant/variant.hpp>

void OmniCharacter::_bind_methods() {
	ClassDB::bind_method(D_METHOD("set_animation_packs", "animation_packs"), &OmniCharacter::set_animation_packs);
	ClassDB::bind_method(D_METHOD("get_animation_packs"), &OmniCharacter::get_animation_packs);

	ADD_PROPERTY(PropertyInfo(Variant::ARRAY, "animation_packs", godot::PROPERTY_HINT_ARRAY_TYPE, "Pack"), "set_animation_packs", "get_animation_packs");
}

void OmniCharacter::set_animation_packs(const TypedArray<Pack> &p_animation_packs) {

	animationPacks = p_animation_packs;
}

TypedArray<Pack> OmniCharacter::get_animation_packs() const {
	return animationPacks;
}
