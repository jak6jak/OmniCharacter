#pragma once

#include "Framework/Pack.h"
#include "godot_cpp/classes/character_body3d.hpp"
#include "godot_cpp/classes/wrapped.hpp"
#include "godot_cpp/variant/variant.hpp"
#include <godot_cpp/templates/vector.hpp>
#include <godot_cpp/variant/array.hpp>
#include <godot_cpp/variant/typed_array.hpp>

using namespace godot;

class OmniCharacter : public CharacterBody3D {
	GDCLASS(OmniCharacter, CharacterBody3D)

protected:
	static void _bind_methods();

private:
	TypedArray<Pack> animationPacks;

public:
	OmniCharacter() = default;
	~OmniCharacter() override = default;
	void set_animation_packs(const TypedArray<Pack> &p_animation_packs);
	TypedArray<Pack> get_animation_packs() const;
};
