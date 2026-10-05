

--thanks to the OVN crew for letting me scavenge campaign start scripts
--tried that with Mixu's but I'm too much of a gnoblar to understand gnoblar scripts.

local function spawn_new_force()
--cm:kill_all_armies_for_faction(cm:get_faction("wh_main_teb_estalia")) --add equivalent to kill the others
	cm:create_force_with_general(
		"cr_teb_magritta", -- faction_key,
        "teb_conqui_royal_guard,teb_conqui_royal_guard,teb_irrana,teb_conqui_adventurers,teb_billmen,teb_billmen,teb_billmen,teb_conqui_lancers,teb_conqui_riders,wh_main_emp_art_mortar", -- unit_list, 2300 skaven to kill and they have warp bullshit
		"cr_oldworld_region_magritta", -- region_key,
		440, -- x,                    -- magritta is 303,558 (nearby vamps are in 328,580)
		900, -- y,
		"general", -- type,
		"teb_lupio", -- subtype,
		"names_name_3011980",	-- first name
		"",
		"names_name_3011981",	-- last name
		"", -- name4,
		true,-- make_faction_leader,
        function(cqi) -- callback
            local str = "character_cqi:" .. cqi
            cm:set_character_immortality(str, true);
            cm:set_character_unique(str, true);
            cm:apply_effect_bundle_to_characters_force("wh2_main_bundle_military_movement_range_positive", cqi, 5, true)
            cm:apply_effect_bundle_to_characters_force("wh2_main_incident_hef_replenish_rate_up", cqi, 5, true)
        end
	)
end

local function new_game_startup()
    local teb_faction_string = "cr_teb_magritta"
	local teb_faction = cm:get_faction(teb_faction_string)

    if not teb_faction then return end

    local to_kill_cqi = nil
    local mixer_teb_faction_leader = teb_faction:faction_leader()

	if mixer_teb_faction_leader and not mixer_teb_faction_leader:is_null_interface() then
		to_kill_cqi = mixer_teb_faction_leader:command_queue_index()
	end

    spawn_new_force()

    --local teb_faction_region = cm:get_region("wh3_main_combi_region_miragliano")


--	cm:instantly_set_settlement_primary_slot_level(teb_faction_region:settlement(), 3)
	--cm:heal_garrison(teb_faction_region:cqi()); --only for capital region
-- ACTUALLY need this to heal all custom units in garris.
    for _, teb_faction_region in model_pairs (teb_faction:region_list()) do
        cm:heal_garrison(teb_faction_region:cqi());
    end
    local teb_region_barracks = cm:get_region("cr_oldworld_region_magritta")
	local target_slot = teb_region_barracks:slot_list():item_at(2)
   cm:instantly_upgrade_building_in_region(target_slot, "teb_mil_city_1")
    cm:create_agent(
        "cr_teb_magritta",
        "dignitary",
        "teb_priestess",
		440, -- x,
		898, -- y,
        false
    )

    local unit_count = 1 -- card32 count
    local rcp = 20 -- float32 replenishment_chance_percentage
    local max_units = 1 -- int32 max_units
    local murpt = 0.1 -- float32 max_units_replenished_per_turn
    local xp_level = 0 -- card32 xp_level
    local frr = "" -- (may be empty) String faction_restricted_record
    local srr = "" -- (may be empty) String subculture_restricted_record
    local trr = "" -- (may be empty) String tech_restricted_record
    local units = {
                    "teb_ricco",
                    "teb_alcatani",
                    "teb_pirazzo",
                    "teb_leopard",
                    "teb_venators",
                    "teb_vespero",
                    "teb_marksmen",
                    "teb_besiegers",
           --         "teb_muktar",
            --        "teb_amazons",
                    "teb_cursed",
                    "teb_origo",
                    "teb_manflayers",
                    "teb_asarnil",
                    "teb_tichi",
                    --"teb_bronzino_limbered",
                    "teb_bronzino",
                    "teb_birdmen", --MIND THE COMMA IF ROC ARE INCLUDED
--ROCs 
--remember that the game WILL CRASH if you script a bunch of RORs and they all unlock at level 1,which is pertinent for ROCs
--this may also be pertinent for NWC and Colombo.

                        "teb_roc_dieterfist",
                        "teb_roc_estragon",
                        "teb_roc_verena",
                        "teb_roc_strygos",
                        "teb_roc_broswords",
                        "teb_roc_gwatch",
                        "teb_roc_kotss",
                        "teb_roc_organ",
                        "teb_roc_gunn",
                        "teb_roc_montecastello",
                        "teb_tank_ror"
        
    }

     --cm:change_corruption_in_province_by("wh3_main_combi_province_gianthome_mountains","wh3_main_corruption_chaos", -30, "events")
    
	cm:create_force_with_general(
		"cr_teb_magritta", -- faction_key,
		"teb_militia_spearmen,teb_kotrs,teb_handgunners,teb_billmen", -- unit_list, tiny army but they're supposed to have garrison support and recruit later
		"cr_oldworld_region_magritta", -- region_key,
		269, -- x,                    -- magritta is 303,558 (nearby vamps are in 328,580)
		851, -- y,
		"general", -- type,
		"teb_merc_general", -- subtype,
		"names_name_3011992",	-- first name
		"",
		"",	-- last name
		"", -- name4,
		false,-- make_faction_leader,
        function(cqi)
            local char_str = cm:char_lookup_str(cqi);
            cm:force_add_trait(char_str, "teb_trait_alejandro");        
        end
	)

        if teb_faction:is_human() then--give the backup army something to do before the vamps attack
    cm:create_force_with_general(
				"wh2_twa03_def_rakarth",
                "wh2_main_def_inf_black_ark_corsairs_0,wh2_main_def_inf_black_ark_corsairs_0,wh2_main_def_inf_black_ark_corsairs_0,wh2_main_def_inf_black_ark_corsairs_1,wh2_main_def_inf_harpies",
				"cr_oldworld_region_magritta",
                259, -- x,                    -- magritta is 303,558 (nearby vamps are in 328,580)
                849, -- y,
                "general", -- type,
                "wh2_main_def_dreadlord", -- subtype,
                "",	-- first name
                "",
                "",	-- last name
                "", -- name4,
				false,
				function(cqi)
					cm:disable_movement_for_character("character_cqi:" .. cqi)
				end
			)
        end

        cm:force_declare_war("wh3_main_skv_clan_carrion", "cr_teb_magritta", false, false)
        cm:force_declare_war("wh2_twa03_def_rakarth", "cr_teb_magritta", false, false)
                cm:make_diplomacy_available("cr_teb_magritta", "cr_teb_bilbali")
                cm:make_diplomacy_available("cr_teb_magritta", "cr_teb_tobaro")



    for _, unit in ipairs(units) do
        cm:add_unit_to_faction_mercenary_pool(
            teb_faction,
            unit,
            "wh3_main_regiments_of_renown_pool",
            unit_count,
            rcp,
            max_units,
            murpt,
            frr,
            srr,
            trr,
            true,
            unit
        )
    end
    
    cm:callback(function()
        if to_kill_cqi then
            local str = "character_cqi:" .. to_kill_cqi
            cm:set_character_immortality(str, false)
            cm:kill_character_and_commanded_unit(str, true)
        end
    end, 0)

        cm:callback(
        function()
            cm:show_message_event(
                teb_faction_string,
                "event_feed_strings_text_wh2_scripted_event_how_they_play_title",
                "factions_screen_name_" .. teb_faction_string,
                "event_feed_strings_text_teb_how_they_play",
                true,
                3011
            );
        end,
        1
    )           
end



cm:add_first_tick_callback(
	function()
        pcall(function()
            mixer_set_faction_trait("cr_teb_magritta", "teb_lord_trait_teb_lupio", true)
        end)
		if cm:is_new_game() then
			if cm:get_campaign_name() == "cr_oldworldclassic" then
				local ok, err =
					pcall(
					function()
						new_game_startup()
					end
				)
				if not ok then
					script_error(err)
				end
			end
		end
	end
)