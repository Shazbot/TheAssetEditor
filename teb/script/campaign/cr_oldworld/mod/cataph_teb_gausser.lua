

--thanks to the OVN crew for letting me scavenge campaign start scripts
--tried that with Mixu's but I'm too much of a gnoblar to understand gnoblar scripts.

local function spawn_new_force()
--cm:kill_all_armies_for_faction(cm:get_faction("wh_main_teb_border_princes")) --add equivalent to kill the others
	cm:create_force_with_general(
		"cr_teb_central_confederacy", -- faction_key,
		"wh_main_emp_art_great_cannon,teb_freelance_knights,teb_militia_spearmen,teb_pikemen,teb_pikemen,wh_dlc06_dwf_inf_bugmans_rangers_0,wh_main_dwf_inf_dwarf_warrior_1,teb_light_scouts", -- unit_list
		"cr_oldworld_region_khypris", -- region_key,
		850, -- x,        --khypris is 850,533
		832, -- y,
		"general", -- type,
		"teb_gausser", -- subtype,
		"names_name_2147344026",	-- first name
		"",
		"names_name_2147344023",	-- last name
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
    local teb_faction_string = "cr_teb_central_confederacy"
	local teb_faction = cm:get_faction(teb_faction_string)

    if not teb_faction then return end

    local to_kill_cqi = nil
    local mixer_teb_faction_leader = teb_faction:faction_leader()

	if mixer_teb_faction_leader and not mixer_teb_faction_leader:is_null_interface() then
		to_kill_cqi = mixer_teb_faction_leader:command_queue_index()
	end

    spawn_new_force()

    --local galbaraz = cm:get_region("cr_oldworld_region_khypris")

--  cm:transfer_region_to_faction("wh3_main_combi_region_akendorf", "cr_teb_central_confederacy")   


	--cm:instantly_set_settlement_primary_slot_level(galbaraz:settlement(), 3)
	--cm:heal_garrison(teb_faction_region:cqi()); --only for capital region
-- ACTUALLY need this to heal all custom units in garris.
    for _, teb_faction_region in model_pairs (teb_faction:region_list()) do
        cm:heal_garrison(teb_faction_region:cqi());
    end

    cm:create_agent(
        "cr_teb_central_confederacy",
        "dignitary",
        "teb_priestess",
		851, -- x,
		836, -- y,
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

        if teb_faction:is_human() then
    cm:create_force_with_general(
				"cr_grn_yellow_eye_tribe",
                "wh_main_grn_mon_giant,wh_main_grn_cav_goblin_wolf_riders_0,wh_main_grn_inf_goblin_archers,wh_main_grn_inf_goblin_archers,wh_main_grn_inf_goblin_spearmen,wh_main_grn_inf_goblin_spearmen,wh_main_grn_inf_goblin_spearmen,wh_main_grn_inf_goblin_spearmen,wh_main_grn_inf_goblin_spearmen",
				"cr_oldworld_region_magritta",
                850, -- x,        --khypris is 850,533
                827, -- y,
                "general", -- type,
                "wh_main_grn_goblin_great_shaman", -- subtype,
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

        cm:force_declare_war("cr_grn_yellow_eye_tribe", "cr_teb_central_confederacy", false, false)
--        cm:force_declare_war("wh_main_grn_black_venom", "cr_teb_central_confederacy", false, false)
        cm:force_make_trade_agreement("wh_main_dwf_barak_varr", "cr_teb_central_confederacy")
        cm:force_make_trade_agreement("wh2_main_brt_thegans_crusaders", "cr_teb_central_confederacy")
        cm:make_diplomacy_available("wh_main_dwf_barak_varr", "cr_teb_central_confederacy")
        cm:make_diplomacy_available("wh2_main_brt_thegans_crusaders", "cr_teb_central_confederacy")
--        cm:apply_dilemma_diplomatic_bonus("wh_main_dwf_karak_izor", "cr_teb_central_confederacy", 6) -- this to compensate for Belegar getting pissy about vanilla BP.



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
            mixer_set_faction_trait("cr_teb_central_confederacy", "teb_lord_trait_teb_gausser", true)
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