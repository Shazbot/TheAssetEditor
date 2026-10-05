grudge_cycle.factors.culture_actions["mixer_teb_southern_realms"]="cataph_teb_actions"--this is the DB key for grudge factors
grudge_cycle.cultures["cataph_teb"]="mixer_teb_southern_realms"

--------Nemesis Crown
table.insert(nemesis_crown.cultures, "mixer_teb_southern_realms")


--this stuff probably needs to be nuked if it ever gets added by Mixer at the origin of new cultures.




local function teb_vassal_thingy()
	if cm:is_new_game() then
        cm:force_diplomacy("culture:mixer_teb_southern_realms", "all", "vassal", true, true, true)
        cm:force_diplomacy("culture:mixer_teb_southern_realms", "faction:wh3_dlc26_ogr_golgfag", "vassal", false, false, false)
        cm:force_diplomacy("faction:wh3_dlc26_ogr_golgfag", "culture:mixer_teb_southern_realms", "vassal", false, false, false)
	end
end

cm:add_first_tick_callback(teb_vassal_thingy)
