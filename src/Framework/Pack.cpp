#include "Pack.h"

void Pack::_bind_methods() {
	godot::ClassDB::bind_method(D_METHOD("print_type", "variant"), &Pack::print_type);
}

void Pack::print_type(const Variant &p_variant) const {
	print_line(vformat("Type: %d", p_variant.get_type()));
}
