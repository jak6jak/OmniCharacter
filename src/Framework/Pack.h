#pragma once

#include "godot_cpp/classes/wrapped.hpp"
#include "godot_cpp/variant/variant.hpp"
#include <godot_cpp/classes/resource.hpp>
#include <godot_cpp/variant/string.hpp>
#include <godot_cpp/variant/string_name.hpp>

using namespace godot;

class Pack : public Resource {
	GDCLASS(Pack, Resource)

protected:
	static void _bind_methods();

private:
	StringName pack_name;

public:
	Pack() = default;
	~Pack() override = default;

	StringName get_pack_name() const { return pack_name; }
	void set_pack_name(const StringName &p_name) { pack_name = p_name; }
};
