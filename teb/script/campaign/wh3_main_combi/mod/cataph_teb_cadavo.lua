

--thanks to the OVN crew for letting me scavenge campaign start scripts
--tried that with Mixu's but I'm too much of a gnoblar to understand gnoblar scripts.

local function spawn_new_force()
cm:kill_all_armies_for_faction(cm:get_faction("wh2_main_emp_new_world_colonies")) --add equivalent to kill the others
	cm:create_force_with_general(
		"mixer_teb_new_world_colonies", -- faction_key,
		"teb_light_cannon,teb_swash,teb_xbowmen,teb_pikemen,teb_pikemen,wh3_main_ogr_inf_maneaters_3,teb_conqui_riders", 
		"wh3_main_combi_region_port_reaver", -- region_key,
		92, -- x,
		485, -- y,
		"general", -- type,
		"teb_cadavo", -- subtype,
		"names_name_992004",	-- first name
		"",
		"names_name_992005",	-- last name
		"", -- name4,
		true,-- make_faction_leader,
        function(cqi) -- callback
            local str = "character_cqi:" .. cqi
            cm:set_character_immortality(str, true);
            cm:set_character_unique(str, true);
        end
	)
end

local function new_game_startup()
    local teb_faction_string = "mixer_teb_new_world_colonies"
	local teb_faction = cm:get_faction(teb_faction_string)

    if not teb_faction then return end

    local to_kill_cqi = nil
    local mixer_teb_faction_leader = teb_faction:faction_leader()

	if mixer_teb_faction_leader and not mixer_teb_faction_leader:is_null_interface() then
		to_kill_cqi = mixer_teb_faction_leader:command_queue_index()
	end

    spawn_new_force()

    
 --   local fuming_serpent = cm:get_region("wh3_main_combi_region_fuming_serpent")

  cm:transfer_region_to_faction("wh3_main_combi_region_port_reaver", "mixer_teb_new_world_colonies")         
  cm:transfer_region_to_faction("wh3_main_combi_region_monument_of_the_moon", "mixer_teb_new_world_colonies")   
  cm:transfer_region_to_faction("wh3_main_combi_region_swamp_town", "mixer_teb_new_world_colonies")   

--	cm:instantly_set_settlement_primary_slot_level(fuming_serpent:settlement(), 1)
    

    for _, teb_faction_region in model_pairs (teb_faction:region_list()) do
        cm:heal_garrison(teb_faction_region:cqi());
    end

    cm:create_agent(
        "mixer_teb_new_world_colonies",
        "champion",
        "teb_merc_captain",
		98, -- x,
		484, -- y,
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
				"wh2_main_rogue_scourge_of_aquitaine",
                "wh_dlc07_brt_cav_knights_errant_0,wh2_dlc11_cst_inf_sartosa_free_company_0,wh_main_brt_inf_peasant_bowmen,teb_xbowmen,teb_pikemen,wh_main_brt_inf_men_at_arms,wh_main_brt_inf_men_at_arms,wh_main_brt_inf_men_at_arms", 
				"wh3_main_combi_region_port_reaver",
                92, -- x,
                488, -- y,
                "general", -- type,
                "teb_cadavo", -- subtype,
                "names_name_997018",	-- first name
                "",
                "",	-- last name
                "", -- name4,
				false,
				function(cqi)
					cm:apply_effect_bundle_to_characters_force("wh_main_bundle_military_upkeep_free_force", cqi, -1, true)     
					cm:disable_movement_for_character("character_cqi:" .. cqi)
				end
			)

			--THE WULFRIK SITUATION after 7.0
    local wulfrik_faction_string = "wh_dlc08_nor_norsca"
    local wulfrik_faction = cm:get_faction(wulfrik_faction_string)
    
    if wulfrik_faction:is_human() then --gives Wulfrik something to bully since for some reason he's by Monument of the Moon

        cm:create_force_with_general(
            "mixer_teb_new_world_colonies", -- faction_key,
            "teb_militia_spearmen,teb_militia_spearmen,teb_billmen,teb_xbowmen,teb_xbowmen,teb_xbowmen",
            "wh3_main_combi_region_port_reaver", -- region_key,
                147,
                464, --
            "general", -- type,
            "teb_merc_general", -- subtype,
            "names_name_3012120",    -- first name --changed to Alejandro
            "",
            "",    -- last name
            "", -- name4,
            false,-- make_faction_leader,
            function(cqi) -- callback
                    local char_str = cm:char_lookup_str(cqi);
                    cm:disable_movement_for_character("character_cqi:" .. cqi)
					cm:apply_effect_bundle_to_characters_force("wh_main_bundle_military_upkeep_free_force", cqi, 5, true)
                    cm:force_add_trait(char_str, "teb_trait_alejandro");  
            end
        )

        cm:force_declare_war("mixer_teb_new_world_colonies", "wh_dlc08_nor_norsca", false, false)
   
    end 
    
    
    
    
        cm:force_declare_war("wh2_main_rogue_scourge_of_aquitaine", "mixer_teb_new_world_colonies", false, false)
        cm:force_declare_war("wh2_main_nor_skeggi", "mixer_teb_new_world_colonies", false, false)
        cm:make_diplomacy_available("mixer_teb_tilea", "mixer_teb_new_world_colonies")
        cm:make_diplomacy_available("mixer_teb_bilbali", "mixer_teb_new_world_colonies")--trade forced in Lucy's file for timeline
        cm:make_diplomacy_available("mixer_teb_estalia", "mixer_teb_new_world_colonies")




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
            mixer_set_faction_trait("mixer_teb_new_world_colonies", "teb_lord_trait_teb_cadavo", true)
        end)
		if cm:is_new_game() then
			if cm:get_campaign_name() == "main_warhammer" then
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