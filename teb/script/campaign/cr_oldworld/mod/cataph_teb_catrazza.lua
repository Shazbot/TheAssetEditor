

--thanks to the OVN crew for letting me scavenge campaign start scripts
--tried that with Mixu's but I'm too much of a gnoblar to understand gnoblar scripts.
-- teb_merc_general_camp

local function spawn_new_force()
	cm:create_force_with_general(
		"mixer_teb_catrazza", -- faction_key,
		"teb_montante_greatswords,teb_carabiniers,teb_xbow_cav,teb_pikemen,teb_pikemen,teb_xbowmen,teb_paymaster,teb_galloper,wh3_main_ogr_inf_ironguts_0", -- needs to kill 15 units in a siege (of which 4 jade wars)
		"cr_oldworld_region_spice_market", -- region_key,
		2000, -- x,
		628, -- y,
		"general", -- type,
		"teb_catrazza", -- subtype,
		"names_name_2147356961",	-- first name
		"",
		"names_name_2147356691",	-- last name
		"", -- name4,
		true,-- make_faction_leader,
        function(cqi) -- callback
            local str = "character_cqi:" .. cqi
            cm:set_character_immortality(str, true);
            cm:set_character_unique(str, true);
            cm:apply_effect_bundle_to_characters_force("wh3_main_ie_scripted_endgame_force_immune_to_regionless_attrition", cqi, 5, true)
        end
	)
end


local function new_game_startup()
    local teb_faction_string = "mixer_teb_catrazza"
	local teb_faction = cm:get_faction(teb_faction_string)

    if not teb_faction then return end

    local to_kill_cqi = nil
    local mixer_teb_faction_leader = teb_faction:faction_leader()

	if mixer_teb_faction_leader and not mixer_teb_faction_leader:is_null_interface() then
		to_kill_cqi = mixer_teb_faction_leader:command_queue_index()
	end

    spawn_new_force()


-- ACTUALLY need this to heal all custom units in garris.
    for _, teb_faction_region in model_pairs (teb_faction:region_list()) do
        cm:heal_garrison(teb_faction_region:cqi());
    end

    cm:create_agent(
        "mixer_teb_catrazza",
        "dignitary",
        "teb_priestess",
		1998, -- x,
		630, -- y,
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
 
 	cm:create_force_with_general(
		"mixer_teb_catrazza", -- faction_key,
		"wh3_main_ogr_inf_maneaters_1,teb_militia_spearmen,teb_handgunners,teb_billmen", -- unit_list
		"cr_oldworld_region_spice_market", -- region_key,
		1979, -- x,                    
		662, -- y,
		"general", -- type,
		"teb_merc_general_camp", -- subtype,
		"",	-- first name
		"",
		"",	-- last name
		"", -- name4,
		false,-- make_faction_leader,
        function(cqi)
            local char_str = cm:char_lookup_str(cqi);
					cm:force_character_force_into_stance(cm:char_lookup_str(cqi), "MILITARY_FORCE_ACTIVE_STANCE_TYPE_SET_CAMP")      
        end
	)

 
 
 
        cm:force_declare_war("wh3_main_cth_burning_wind_nomads", "mixer_teb_catrazza", false, false)
        cm:make_diplomacy_available("cr_teb_miragliano", "mixer_teb_catrazza")
        cm:force_make_trade_agreement("cr_teb_miragliano", "mixer_teb_catrazza")      
        --cm:force_make_trade_agreement("wh3_main_cth_the_western_provinces", "mixer_teb_catrazza") --Ironbro is not around is he
        --cm:make_diplomacy_available("cr_teb_bilbali", "mixer_teb_catrazza")       --re-enable when Lucy gets her start
        cm:make_diplomacy_available("cr_teb_magritta", "mixer_teb_catrazza")
        cm:make_diplomacy_available("cr_teb_pigbarter", "mixer_teb_catrazza")
        cm:make_diplomacy_available("wh3_main_ogr_eyebiter", "mixer_teb_catrazza")



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
            mixer_set_faction_trait("mixer_teb_catrazza", "teb_lord_trait_teb_catrazza", true)
        end)
		if cm:is_new_game() then
			if cm:get_campaign_name() == "cr_oldworld" then
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