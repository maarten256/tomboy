
using System;
using Gtk;
using Mono.Unix;
using Tomboy;

namespace Tomboy.Tasks
{
	public class TaskListWindow : ForcedPresentWindow
	{
		enum SortColumn : int
		{
			Summary,
			DueDate,
			CompletionDate,
			Priority,
			OriginNote
		};
		
		TaskManager manager;
		
		Gtk.ActionGroup action_group;
		uint menubar_ui;
		
		Gtk.MenuBar menu_bar;
		Gtk.Label task_count;
		Gtk.ScrolledWindow tasks_sw;
		Gtk.VBox content_vbox;

		Gtk.TreeView tree;
		Gtk.TreeModelSort store_sort;
		SortColumn sort_column;
		Gtk.TreeViewColumn note_column;
		Gtk.TreeViewColumn summary_column;
		Gtk.TreeViewColumn due_date_column;
		Gtk.TreeViewColumn completion_column;
		Gtk.TreeViewColumn priority_column;
		
		Gtk.Menu ctx_menu;
		
		// Use this to select the task that was created inside
		// this window.
		bool expecting_newly_created_task;

		static TaskListWindow instance;
		
		static Gdk.Pixbuf note_pixbuf;
		
		static TaskListWindow ()
		{
			note_pixbuf = GuiUtils.GetIcon ("tomboy-note", 8);
		}

		public static TaskListWindow GetInstance (TaskManager manager)
		{
			if (instance == null)
				instance = new TaskListWindow (manager);
			System.Diagnostics.Debug.Assert (
				instance.manager == manager, 
				"Multiple TaskManagers not supported");
			return instance;
		}

		protected TaskListWindow (TaskManager manager)
			: base (Catalog.GetString ("To Do List"))
		{
			this.manager = manager;
			this.IconName = "tomboy";
			this.SetDefaultSize (500, 300);
			this.sort_column = SortColumn.CompletionDate;
			
			expecting_newly_created_task = false;
			
			AddAccelGroup (Tomboy.ActionManager.UI.AccelGroup);

			action_group = new Gtk.ActionGroup ("TaskList");
			action_group.Add (new Gtk.ActionEntry [] {
				new Gtk.ActionEntry ("TaskListFileMenuAction", null,
					Catalog.GetString ("_File"), null, null, null),

				new Gtk.ActionEntry ("NewTaskAction", Gtk.Stock.New,
					Catalog.GetString ("New _Task"), "<Control>T",
					Catalog.GetString ("Create a new task"), null),
				
				new Gtk.ActionEntry ("OpenTaskAction", String.Empty,
					Catalog.GetString ("_Options..."), "<Control>O",
					Catalog.GetString ("Open the selected task"), null),
				
				new Gtk.ActionEntry ("CloseTaskListWindowAction", Gtk.Stock.Close,
					Catalog.GetString ("_Close"), "<Control>W",
					Catalog.GetString ("Close this window"), null),
				
				new Gtk.ActionEntry ("TaskListEditMenuAction", null,
					Catalog.GetString ("_Edit"), null, null, null),
					
				new Gtk.ActionEntry ("DeleteTaskAction", Gtk.Stock.Preferences,
					Catalog.GetString ("_Delete"), "Delete",
					Catalog.GetString ("Delete the selected task"), null),
				
				new Gtk.ActionEntry ("OpenOriginNoteAction", null,
					Catalog.GetString ("Open Associated _Note"), null,
					Catalog.GetString ("Open the note containing the task"), null),

				new Gtk.ActionEntry ("TaskListHelpMenuAction", null,
					Catalog.GetString ("_Help"), null, null, null),
					
				new Gtk.ActionEntry ("ShowTaskHelpAction", Gtk.Stock.Help,
					Catalog.GetString ("_Contents"), "F1",
					Catalog.GetString ("Tasks Help"), null)
			});
			
			Tomboy.ActionManager.UI.InsertActionGroup (action_group, 0);

			menu_bar = CreateMenuBar ();
			
			MakeTasksTree ();
			tree.Show ();

			// Update on changes to tasks
			TaskManager.TaskAdded += OnTaskAdded;
			TaskManager.TaskDeleted += OnTaskDeleted;
			TaskManager.TaskStatusChanged += OnTaskStatusChanged;

			tasks_sw = new Gtk.ScrolledWindow ();
			tasks_sw.ShadowType = Gtk.ShadowType.In;
			tasks_sw.VscrollbarPolicy = Gtk.PolicyType.Automatic;
			tasks_sw.HscrollbarPolicy = Gtk.PolicyType.Automatic;

			// Reign in the window size if there are notes with long
			// names, or a lot of notes...

			Gtk.Requisition tree_req = tree.SizeRequest ();
			if (tree_req.Height > 420)
				tasks_sw.HeightRequest = 420;

			if (tree_req.Width > 480)
				tasks_sw.WidthRequest = 480;

			tasks_sw.Add (tree);
			tasks_sw.Show ();
			
			task_count = new Gtk.Label ();
			task_count.Xalign = 0;
			task_count.Show ();

			Gtk.HBox status_box = new Gtk.HBox (false, 8);
			status_box.PackStart (task_count, true, true, 0);
			status_box.Show ();
			
			Gtk.VBox vbox = new Gtk.VBox (false, 8);
			vbox.BorderWidth = 6;
			vbox.PackStart (tasks_sw, true, true, 0);
			vbox.PackStart (status_box, false, false, 0);
			vbox.Show ();

			// Use another VBox to place the MenuBar
			// right at thetop of the window.
			content_vbox = new Gtk.VBox (false, 0);
			content_vbox.PackStart (menu_bar, false, false, 0);
			content_vbox.PackStart (vbox, true, true, 0);
			content_vbox.Show ();

			this.Add (content_vbox);
			this.DeleteEvent += OnDelete;
			this.KeyPressEvent += OnKeyPressed; // For Escape

			SetUpTreeModel ();
		}

		Gtk.MenuBar CreateMenuBar ()
		{
			ActionManager am = Tomboy.ActionManager;
			menubar_ui = Tomboy.ActionManager.UI.AddUiFromResource (
					"TasksUIManagerLayout.xml");
			
			Gtk.MenuBar menubar =
				Tomboy.ActionManager.GetWidget ("/TaskListWindowMenubar") as Gtk.MenuBar;
			
			am ["NewTaskAction"].Activated += OnNewTask;
			am ["OpenTaskAction"].Activated += OnOpenTask;
			am ["OpenOriginNoteAction"].Activated += OnOpenOriginNote;
			am ["OpenOriginNoteAction"].Sensitive = false;
			am ["CloseTaskListWindowAction"].Activated += OnCloseWindow;
			am ["DeleteTaskAction"].Activated += OnDeleteTask;
			am ["ShowTaskHelpAction"].Activated += OnShowHelp;
			
			return menubar;
		}
		
		void MakeTasksTree ()
		{
			tree = new Gtk.TreeView ();
			tree.HeadersVisible = true;
			tree.RulesHint = true;
			tree.RowActivated += OnRowActivated;
			tree.Selection.Changed += OnSelectionChanged;
			tree.ButtonPressEvent += OnButtonPressed;
			
			// Columns: OriginNote, Completion Status Summary, Due Date (No Date/Date), Completed (No Date/Date), Priority

			Gtk.CellRenderer renderer;
			
			///
			/// OriginNote
			///
			note_column = new Gtk.TreeViewColumn ();
			note_column.Title = string.Empty;
			note_column.Sizing = Gtk.TreeViewColumnSizing.Autosize;
			note_column.Resizable = false;
			note_column.Clickable = true;
			note_column.Clicked += OnNoteColumnClicked;
			note_column.Reorderable = true;
			note_column.SortIndicator = true;
			
			renderer = new Gtk.CellRendererPixbuf ();
			note_column.PackStart (renderer, false);
			note_column.SetCellDataFunc (renderer,
					new Gtk.TreeCellDataFunc (NoteIconCellDataFunc));
			tree.AppendColumn (note_column);
			
			///
			/// Completion Status
			///
			Gtk.TreeViewColumn status = new Gtk.TreeViewColumn ();
			status.Title = string.Empty;
			status.Sizing = Gtk.TreeViewColumnSizing.Autosize;
			status.Resizable = false;
			status.Clickable = true;
			status.Clicked += OnCompletionColumnClicked;
			status.Reorderable = true;
			status.SortIndicator = true;
			
			renderer = new Gtk.CellRendererToggle ();
			(renderer as Gtk.CellRendererToggle).Toggled += OnTaskToggled;
			status.PackStart (renderer, false);
			status.SetCellDataFunc (renderer,
					new Gtk.TreeCellDataFunc (ToggleCellDataFunc));
			tree.AppendColumn (status);
			
			///
			/// Summary
			///
			summary_column = new Gtk.TreeViewColumn ();
			summary_column.Title = Catalog.GetString ("Summary");
			summary_column.MinWidth = 200;
			summary_column.FixedWidth = 200;
			summary_column.Sizing = Gtk.TreeViewColumnSizing.Autosize;
			summary_column.Resizable = true;
			summary_column.Clickable = true;
			summary_column.Clicked += OnSummaryColumnClicked;
			summary_column.Reorderable = true;
			summary_column.SortIndicator = true;
			
			renderer = new Gtk.CellRendererText ();
			(renderer as CellRendererText).Editable = true;
			(renderer as CellRendererText).Ellipsize = Pango.EllipsizeMode.End;
			(renderer as CellRendererText).Edited += OnTaskSummaryEdited;
			renderer.Xalign = 0.0f;
			summary_column.PackStart (renderer, true);
			summary_column.SetCellDataFunc (renderer,
					new Gtk.TreeCellDataFunc (SummaryCellDataFunc));
			tree.AppendColumn (summary_column);
			
			///
			/// Due Date
			///
			due_date_column = new Gtk.TreeViewColumn ();
			due_date_column.Title = Catalog.GetString ("Due Date");
			due_date_column.Sizing = Gtk.TreeViewColumnSizing.Autosize;
			due_date_column.Resizable = false;
			due_date_column.Clickable = true;
			due_date_column.Clicked += OnDueDateColumnClicked;
			due_date_column.Reorderable = true;
			due_date_column.SortIndicator = true;
			
			renderer = new Gtk.Extras.CellRendererDate ();
			(renderer as Gtk.Extras.CellRendererDate).Editable = true;
			(renderer as Gtk.Extras.CellRendererDate).Edited += OnDueDateEdited;
			(renderer as Gtk.Extras.CellRendererDate).ShowTime = false;
			renderer.Xalign = 0.0f;
			due_date_column.PackStart (renderer, true);
			due_date_column.SetCellDataFunc (renderer,
					new Gtk.TreeCellDataFunc (DueDateCellDataFunc));
			tree.AppendColumn (due_date_column);
			
			///
			/// Completion Date
			///
			completion_column = new Gtk.TreeViewColumn ();
			completion_column.Title = Catalog.GetString ("Completion Date");
			completion_column.Sizing = Gtk.TreeViewColumnSizing.Autosize;
			completion_column.Resizable = false;
			completion_column.Clickable = true;
			completion_column.Clicked += OnCompletionColumnClicked;
			completion_column.Reorderable = true;
			completion_column.SortIndicator = true;
			
			renderer = new Gtk.Extras.CellRendererDate ();
			(renderer as Gtk.Extras.CellRendererDate).Editable = false;
			(renderer as Gtk.Extras.CellRendererDate).ShowTime = true;
			renderer.Xalign = 0.0f;
			completion_column.PackStart (renderer, true);
			completion_column.SetCellDataFunc (renderer,
					new Gtk.TreeCellDataFunc (CompletionDateCellDataFunc));
			tree.AppendColumn (completion_column);
			
			///
			/// Priority
			///
			priority_column = new Gtk.TreeViewColumn ();
			priority_column.Title = Catalog.GetString ("Priority");
			priority_column.Sizing = Gtk.TreeViewColumnSizing.Autosize;
			priority_column.Resizable = false;
			priority_column.Clickable = true;
			priority_column.Clicked += OnPriorityColumnClicked;
			priority_column.Reorderable = true;
			priority_column.SortIndicator = true;
			
			renderer = new Gtk.CellRendererCombo ();
			(renderer as Gtk.CellRendererCombo).Editable = true;
			(renderer as Gtk.CellRendererCombo).HasEntry = false;
			(renderer as Gtk.CellRendererCombo).Edited += OnTaskPriorityEdited;
			Gtk.ListStore priority_store = new Gtk.ListStore (typeof (string));
			priority_store.AppendValues (Catalog.GetString ("None"));
			priority_store.AppendValues (Catalog.GetString ("Low"));
			priority_store.AppendValues (Catalog.GetString ("Normal"));
			priority_store.AppendValues (Catalog.GetString ("High"));
			(renderer as Gtk.CellRendererCombo).Model = priority_store;
			(renderer as Gtk.CellRendererCombo).TextColumn = 0;
			renderer.Xalign = 0.0f;
			priority_column.PackStart (renderer, true);
			priority_column.SetCellDataFunc (renderer,
					new Gtk.TreeCellDataFunc (PriorityCellDataFunc));
			tree.AppendColumn (priority_column);
		}
		
		void NoteIconCellDataFunc (Gtk.TreeViewColumn tree_column,
				Gtk.CellRenderer cell, Gtk.TreeModel tree_model,
				Gtk.TreeIter iter)
		{
			Gtk.CellRendererPixbuf crp = cell as Gtk.CellRendererPixbuf;
			Task task = tree_model.GetValue (iter, 0) as Task;
			if (task != null && task.OriginNoteUri != string.Empty)
				crp.Pixbuf = note_pixbuf;
			else
				crp.Pixbuf = null;
		}

		void ToggleCellDataFunc (Gtk.TreeViewColumn tree_column,
				Gtk.CellRenderer cell, Gtk.TreeModel tree_model,
				Gtk.TreeIter iter)
		{
			Gtk.CellRendererToggle crt = cell as Gtk.CellRendererToggle;
			Task task = tree_model.GetValue (iter, 0) as Task;
			if (task == null)
				crt.Active = false;
			else {
				crt.Active = task.IsComplete;
			}
		}
		
		void SummaryCellDataFunc (Gtk.TreeViewColumn tree_column,
				Gtk.CellRenderer cell, Gtk.TreeModel tree_model,
				Gtk.TreeIter iter)
		{
			Gtk.CellRendererText crt = cell as Gtk.CellRendererText;
			Task task = tree_model.GetValue (iter, 0) as Task;
			if (task == null)
				crt.Text = String.Empty;
			else
				crt.Text = task.Summary;
		}
		
		void DueDateCellDataFunc (Gtk.TreeViewColumn tree_column,
				Gtk.CellRenderer cell, Gtk.TreeModel tree_model,
				Gtk.TreeIter iter)
		{
			Gtk.Extras.CellRendererDate crd = cell as Gtk.Extras.CellRendererDate;
			Task task = tree_model.GetValue (iter, 0) as Task;
			if (task == null)
				crd.Date = DateTime.MinValue;
			else
				crd.Date = task.DueDate;
		}
		
		void CompletionDateCellDataFunc (Gtk.TreeViewColumn tree_column,
				Gtk.CellRenderer cell, Gtk.TreeModel tree_model,
				Gtk.TreeIter iter)
		{
			Gtk.Extras.CellRendererDate crd = cell as Gtk.Extras.CellRendererDate;
			Task task = tree_model.GetValue (iter, 0) as Task;
			if (task == null)
				crd.Date = DateTime.MinValue;
			else
				crd.Date = task.CompletionDate;
		}
		
		void PriorityCellDataFunc (Gtk.TreeViewColumn tree_column,
				Gtk.CellRenderer cell, Gtk.TreeModel tree_model,
				Gtk.TreeIter iter)
		{
			// FIXME: Add bold (for high), light (for None), and also colors to priority?
			Gtk.CellRendererCombo crc = cell as Gtk.CellRendererCombo;
			Task task = tree_model.GetValue (iter, 0) as Task;
			switch (task.Priority) {
			case TaskPriority.Low:
				crc.Text = Catalog.GetString ("Low");
				break;
			case TaskPriority.Normal:
				crc.Text = Catalog.GetString ("Normal");
				break;
			case TaskPriority.High:
				crc.Text = Catalog.GetString ("High");
				break;
			default:
				crc.Text = Catalog.GetString ("None");
				break;
			}
		}

		void SetUpTreeModel ()
		{
			store_sort = new Gtk.TreeModelSort (manager.Tasks);
Logger.Debug ("store_sort has {0} columns", store_sort.NColumns);
			store_sort.DefaultSortFunc = 
				new Gtk.TreeIterCompareFunc (TaskSortFunc);
				
			tree.Model = store_sort;
			
			int cnt = tree.Model.IterNChildren ();
			
			task_count.Text = string.Format (
				Catalog.GetPluralString("Total: {0} task",
							"Total: {0} tasks",
							cnt),
				cnt);
		}
		
		void UpdateTaskCount (int total)
		{
			string cnt = string.Format (
				Catalog.GetPluralString("Total: {0} task",
							"Total: {0} tasks",
							total), 
				total);

			task_count.Text = cnt;
		}
		
		void OnSelectionChanged (object sender, EventArgs args)
		{
			Task task = GetSelectedTask ();
			if (task != null) {
				Tomboy.ActionManager ["OpenTaskAction"].Sensitive = true;
				Tomboy.ActionManager ["DeleteTaskAction"].Sensitive = true;
				
				if (task.OriginNoteUri != string.Empty)
					Tomboy.ActionManager ["OpenOriginNoteAction"].Sensitive = true;
				else
					Tomboy.ActionManager ["OpenOriginNoteAction"].Sensitive = false;
			} else {
				Tomboy.ActionManager ["OpenTaskAction"].Sensitive = false;
				Tomboy.ActionManager ["DeleteTaskAction"].Sensitive = false;
			}
		}
		
		[GLib.ConnectBefore]
		void OnButtonPressed (object sender, Gtk.ButtonPressEventArgs args)
		{
			switch (args.Event.Button) {
			case 3: // third mouse button (right-click)
				Gtk.TreePath path = null;
				Gtk.TreeViewColumn column = null;
				
				if (tree.GetPathAtPos ((int) args.Event.X,
						(int) args.Event.Y,
						out path,
						out column) == false)
					break;
				
				Gtk.TreeIter iter;
				if (store_sort.GetIter (out iter, path) == false)
					break;
				
				tree.Selection.SelectIter (iter);
				
				Task task = store_sort.GetValue (iter, 0) as Task;
				if (task == null)
					break;
				
				PopupContextMenuAtLocation (task, (int) args.Event.X,
						(int) args.Event.Y);

				break;
			}
		}
		
		/// <summary>
		/// <param name="task">The selected task</param>
		/// </summary>
		void PopupContextMenuAtLocation (Task task, int x, int y)
		{
			if (ctx_menu == null) {
				ctx_menu = Tomboy.ActionManager.GetWidget (
					"/TaskListWindowContextMenu") as Gtk.Menu;
			}
			
			ctx_menu.ShowAll ();
			Gtk.MenuPositionFunc pos_menu_func = null;
			
			// Set up the funtion to position the context menu
			// if we were called by the keyboard Gdk.Key.Menu.
			if (x == 0 && y == 0)
				pos_menu_func = PositionContextMenu;
				
			ctx_menu.Popup (null, null,
					pos_menu_func,
					0,
					Gtk.Global.CurrentEventTime);
		}
		
		// This is needed for when the user opens
		// the context menu with the keyboard.
		void PositionContextMenu (Gtk.Menu menu,
				out int x, out int y, out bool push_in)
		{
			Gtk.TreeIter iter;
			Gtk.TreePath path;
			Gtk.TreeSelection selection;
			
			// Set default "return" values
			push_in = false; // not used
			x = 0;
			y = 0;
			
			selection = tree.Selection;
			if (!selection.GetSelected (out iter))
				return;
			
			path = store_sort.GetPath (iter);
			
			int pos_x = 0;
			int pos_y = 0;
			
			GetWidgetScreenPos (tree, ref pos_x, ref pos_y);
			Gdk.Rectangle cell_rect = tree.GetCellArea (path, tree.Columns [0]);
			
			// Add 100 to x so it's not be at the far left
			x = pos_x + cell_rect.X + 100;
			y = pos_y + cell_rect.Y;
		}
		
		// Walk the widget hiearchy to figure out
		// where to position the context menu.
		void GetWidgetScreenPos (Gtk.Widget widget, ref int x, ref int y)
		{
			int widget_x;
			int widget_y;
			
			if (widget is Gtk.Window) {
				((Gtk.Window) widget).GetPosition (out widget_x, out widget_y);
			} else {
				GetWidgetScreenPos (widget.Parent, ref x, ref y);
				
				// Special case the TreeView because it adds
				// too much since it's in a scrolled window.
				if (widget == tree) {
					widget_x = 2;
					widget_y = 2;
				} else {
					Gdk.Rectangle widget_rect = widget.Allocation;
					widget_x = widget_rect.X;
					widget_y = widget_rect.Y;
				}
			}
			
			x += widget_x;
			y += widget_y;
		}

		Task GetSelectedTask ()
		{
			Gtk.TreeModel model;
			Gtk.TreeIter iter;

			if (!tree.Selection.GetSelected (out model, out iter))
				return null;

			return (Task) model.GetValue (iter, 0);
		}

		/// <summary>
		/// Create a new task using "New Task NNN" format
		/// </summary>
		void OnNewTask (object sender, EventArgs args)
		{
			int new_num = manager.Tasks.IterNChildren ();
			string summary;
			
			while (true) {
				summary = String.Format (Catalog.GetString ("New Task {0}"), 
							    ++new_num);
				if (manager.Find (summary) == null)
					break;
			}
			
			try {
				expecting_newly_created_task = true;
				manager.Create (summary);
			} catch (Exception e) {
				expecting_newly_created_task = false;
				Logger.Error ("Could not create a new task with summary: {0}:{1}", summary, e.Message);
			}
		}
		
		void OnOpenTask (object sender, EventArgs args)
		{
			Task task = GetSelectedTask ();
			if (task == null)
				return;
			
			TaskOptionsDialog dialog = new TaskOptionsDialog (this, DialogFlags.DestroyWithParent, task);
			dialog.WindowPosition = Gtk.WindowPosition.CenterOnParent;
			dialog.Run ();
			dialog.Destroy ();
		}
		
		void OnDeleteTask (object sender, EventArgs args)
		{
			Task task = GetSelectedTask ();
			if (task == null)
				return;
			
			ShowDeletionDialog (task);
		}
		
		void OnShowHelp (object sender, EventArgs args)
		{
			GuiUtils.ShowHelp ("tomboy.xml", "tasks", Screen, this);
		}
		
		void OnCloseWindow (object sender, EventArgs args)
		{
			// Disconnect external signal handlers to prevent bloweup
			TaskManager.TaskAdded -= OnTaskAdded;
			TaskManager.TaskDeleted -= OnTaskDeleted;
			TaskManager.TaskStatusChanged -= OnTaskStatusChanged;
			
			// The following code has to be done for the MenuBar to
			// appear properly the next time this window is opened.
			if (menu_bar != null) {
				content_vbox.Remove (menu_bar);
				ActionManager am = Tomboy.ActionManager;
				am ["NewTaskAction"].Activated -= OnNewTask;
				am ["OpenTaskAction"].Activated -= OnOpenTask;
				am ["CloseTaskListWindowAction"].Activated -= OnCloseWindow;
				am ["DeleteTaskAction"].Activated -= OnDeleteTask;
				am ["ShowTaskHelpAction"].Activated -= OnShowHelp;
			}
			
			Tomboy.ActionManager.UI.RemoveActionGroup (action_group);
			Tomboy.ActionManager.UI.RemoveUi (menubar_ui);
			
			Hide ();
			Destroy ();
			instance = null;
		}
		
		void OnDelete (object sender, Gtk.DeleteEventArgs args)
		{
			OnCloseWindow (sender, EventArgs.Empty);
			args.RetVal = true;
		}
		
		void OnKeyPressed (object sender, Gtk.KeyPressEventArgs args)
		{
			switch (args.Event.Key) {
			case Gdk.Key.Escape:
				// Allow Escape to close the window
				OnCloseWindow (this, EventArgs.Empty);
				break;
			case Gdk.Key.Menu:
				// Pop up the context menu if a note is selected
				Task task = GetSelectedTask ();
				if (task != null)
					PopupContextMenuAtLocation (task, 0, 0);

				break;
			}
		}
		
		void OnOpenOriginNote (object sender, EventArgs args)
		{
			Gtk.TreeSelection selection;
			Gtk.TreeIter iter;
			
			selection = tree.Selection;
			if (!selection.GetSelected (out iter))
				return;
			
			Task task = store_sort.GetValue (iter, 0) as Task;
			if (task == null || task.OriginNoteUri == string.Empty)
				return;
			
			Note note =
				Tomboy.DefaultNoteManager.FindByUri (task.OriginNoteUri);
			
			if (note == null)
				return;
			
			note.Window.Present ();
		}
		
	//	protected override void OnShown ()
	//	{
	//		base.OnShown ();
	//	}
		
		int TaskSortFunc (Gtk.TreeModel model, Gtk.TreeIter a, Gtk.TreeIter b)
		{
			int result = -1;
			Task task_a = model.GetValue (a, 0) as Task;
			Task task_b = model.GetValue (b, 0) as Task;
			
			switch (sort_column) {
			case SortColumn.CompletionDate:
				result = CompareTasks (task_a, task_b, SortColumn.CompletionDate);
				if (result == 0)
					result = CompareTasks (task_a, task_b, SortColumn.Priority);
				if (result == 0)
					result = CompareTasks (task_a, task_b, SortColumn.Summary);
				if (completion_column.SortOrder == Gtk.SortType.Descending)
					result = result * -1;
				break;
			case SortColumn.DueDate:
				result = CompareTasks (task_a, task_b, SortColumn.DueDate);
				if (result == 0)
					result = CompareTasks (task_a, task_b, SortColumn.CompletionDate);
				if (result == 0)
					result = CompareTasks (task_a, task_b, SortColumn.Priority);
				if (result == 0)
					result = CompareTasks (task_a, task_b, SortColumn.Summary);
				if (due_date_column.SortOrder == Gtk.SortType.Descending)
					result = result * -1;
				break;
			case SortColumn.OriginNote:
				result = CompareTasks (task_a, task_b, SortColumn.OriginNote);
				if (result == 0)
					result = CompareTasks (task_a, task_b, SortColumn.CompletionDate);
				if (result == 0)
					result = CompareTasks (task_a, task_b, SortColumn.Priority);
				if (result == 0)
					result = CompareTasks (task_a, task_b, SortColumn.Summary);
				if (note_column.SortOrder == Gtk.SortType.Descending)
					result = result * -1;
				break;
			case SortColumn.Priority:
				result = CompareTasks (task_a, task_b, SortColumn.Priority);
				if (result == 0)
					result = CompareTasks (task_a, task_b, SortColumn.CompletionDate);
				if (result == 0)
					result = CompareTasks (task_a, task_b, SortColumn.Summary);
				if (priority_column.SortOrder == Gtk.SortType.Descending)
					result = result * -1;
				break;
			case SortColumn.Summary:
				result = CompareTasks (task_a, task_b, SortColumn.Summary);
				if (result == 0)
					result = CompareTasks (task_a, task_b, SortColumn.CompletionDate);
				if (result == 0)
					result = CompareTasks (task_a, task_b, SortColumn.Priority);
				if (summary_column.SortOrder == Gtk.SortType.Descending)
					result = result * -1;
				break;
			}
			
//			return task_a.Summary.CompareTo (task_b.Summary);
			return result;
		}
		
		/// <summary>
		/// Perform a search of the two tasks based on the
		/// SortColumn specified.
		/// </summary>
		int CompareTasks (Task a, Task b, SortColumn sort_type)
		{
			if (a == null)
				return -1;
			if (b == null)
				return 1;
			
			try {
				switch (sort_column) {
				case SortColumn.CompletionDate:
					return DateTime.Compare (a.CompletionDate, b.CompletionDate);
				case SortColumn.DueDate:
					return DateTime.Compare (a.DueDate, b.DueDate);
				case SortColumn.OriginNote:
					return a.OriginNoteUri.CompareTo (b.OriginNoteUri);
				case SortColumn.Priority:
					return (int) a.Priority - (int) b.Priority;
				case SortColumn.Summary:
					return a.Summary.CompareTo (b.Summary);
				}
			} catch (Exception e) {
				Logger.Warn ("Exception in TaskListWindow.CompareTasks ({0}): {1}",
					sort_type, e.Message);
			}
			
			return -1;
		}
		
		void OnRowActivated (object sender, Gtk.RowActivatedArgs args)
		{
			Tomboy.ActionManager ["OpenTaskAction"].Activate ();
		}
		
		void ShowDeletionDialog (Task task)
		{
			HIGMessageDialog dialog = 
				new HIGMessageDialog (
					this,
					Gtk.DialogFlags.DestroyWithParent,
					Gtk.MessageType.Question,
					Gtk.ButtonsType.None,
					Catalog.GetString ("Really delete this task?"),
					Catalog.GetString ("If you delete a task it is " +
							   "permanently lost."));

			Gtk.Button button;

			button = new Gtk.Button (Gtk.Stock.Cancel);
			button.CanDefault = true;
			button.Show ();
			dialog.AddActionWidget (button, Gtk.ResponseType.Cancel);
			dialog.DefaultResponse = Gtk.ResponseType.Cancel;

			button = new Gtk.Button (Gtk.Stock.Delete);
			button.CanDefault = true;
			button.Show ();
			dialog.AddActionWidget (button, 666);

			int result = dialog.Run ();
			if (result == 666) {
				task.Manager.Delete (task);
			}

			dialog.Destroy();
		}

		void OnTaskToggled (object sender, Gtk.ToggledArgs args)
		{
			Gtk.TreePath path = new Gtk.TreePath (args.Path);
			Gtk.TreeIter iter;
			if (store_sort.GetIter (out iter, path) == false)
				return;
			
			Task task = store_sort.GetValue (iter, 0) as Task;
			if (task == null)
				return;
			
			if (task.IsComplete)
				task.ReOpen ();
			else
				task.Complete ();
		}
		
		void OnTaskSummaryEdited (object sender, Gtk.EditedArgs args)
		{
	Logger.Debug ("TaskListWindow.OnTaskSummaryEdited");
			
			Gtk.TreeIter iter;
			Gtk.TreePath path = new TreePath (args.Path);
			if (store_sort.GetIter (out iter, path) == false)
				return;
			
			Task task = store_sort.GetValue (iter, 0) as Task;
			task.Summary = args.NewText;
		}
		
		void OnDueDateEdited (Gtk.Extras.CellRendererDate renderer, string path)
		{
			Logger.Debug ("OnDueDateEdited");
			
			Gtk.TreeIter iter;
			Gtk.TreePath tree_path = new TreePath (path);
			if (store_sort.GetIter (out iter, tree_path) == false)
				return;
			
			Task task = store_sort.GetValue (iter, 0) as Task;
			task.DueDate = renderer.Date;
		}
		
		void OnTaskPriorityEdited (object sender, Gtk.EditedArgs args)
		{
			Gtk.TreeIter iter;
			Gtk.TreePath path = new TreePath (args.Path);
			if (store_sort.GetIter (out iter, path) == false)
				return;
			
			TaskPriority new_priority;
			if (args.NewText.CompareTo (Catalog.GetString ("Low")) == 0)
				new_priority = TaskPriority.Low;
			else if (args.NewText.CompareTo (Catalog.GetString ("Normal")) == 0)
				new_priority = TaskPriority.Normal;
			else if (args.NewText.CompareTo (Catalog.GetString ("High")) == 0)
				new_priority = TaskPriority.High;
			else
				new_priority = TaskPriority.Undefined;

			// Update the priority if it's different
			Task task = store_sort.GetValue (iter, 0) as Task;
			if (task.Priority != new_priority)
				task.Priority = new_priority;
		}
		
		void OnTaskAdded (TaskManager manager, Task task)
		{
			if (expecting_newly_created_task) {
				// A user just created this task inside this window
				expecting_newly_created_task = false;
				
				SelectTask (task);
			}
			
			int cnt = manager.Tasks.IterNChildren ();
			UpdateTaskCount (cnt);
		}
		
		void OnTaskDeleted (TaskManager manager, Task task)
		{
			int cnt = manager.Tasks.IterNChildren ();
			UpdateTaskCount (cnt);
		}
		
		void OnTaskStatusChanged (Task task)
		{
			// FIXME: Eventually update the status bar to include the number of completed notes
		}
		
		void SelectTask (Task task)
		{
			// FIXME: YUCK!  TaskListWindow.SelectTask is pretty ugly (brute force).  Is there a better way to do this?
			Gtk.TreeIter iter;
			
			if (store_sort.IterChildren (out iter) == false)
				return;
			
			do {
				Task iter_task = store_sort.GetValue (iter, 0) as Task;
				if (iter_task == task) {
					// Found it!
					tree.Selection.SelectIter (iter);
					break;
				}
			} while (store_sort.IterNext (ref iter));
		}
		
		void OnNoteColumnClicked (object sender, EventArgs args)
		{
			Logger.Debug ("TaskListWindow.OnNoteColumnClicked");
			OnColumnClicked (note_column, SortColumn.OriginNote);
		}
		
		void OnSummaryColumnClicked (object sender, EventArgs args)
		{
			Logger.Debug ("TaskListWindow.OnSummaryColumnClicked");
			OnColumnClicked (summary_column, SortColumn.Summary);
		}
		
		void OnDueDateColumnClicked (object sender, EventArgs args)
		{
			Logger.Debug ("TaskListWindow.OnDueDateColumnClicked");
			OnColumnClicked (due_date_column, SortColumn.DueDate);
		}
		
		void OnCompletionColumnClicked (object sender, EventArgs args)
		{
			Logger.Debug ("TaskListWindow.OnCompletionColumnClicked");
			OnColumnClicked (completion_column, SortColumn.CompletionDate);
		}
		
		void OnPriorityColumnClicked (object sender, EventArgs args)
		{
			Logger.Debug ("TaskListWindow.OnPriorityColumnClicked");
			OnColumnClicked (priority_column, SortColumn.Priority);
		}
		
		/// <summary>
		/// Handle the click of the specified column by setting the
		/// sort_column and adjusting the sort type (ascending/descending).
		/// </summary>
		void OnColumnClicked (Gtk.TreeViewColumn column, SortColumn column_type)
		{
			if (sort_column != column_type) {
				sort_column = column_type;
			} else {
				if (column.SortOrder == Gtk.SortType.Ascending)
					column.SortOrder = Gtk.SortType.Descending;
				else
					column.SortOrder = Gtk.SortType.Ascending;
			}
			
			// Set up the sort function again so the tree is resorted
			// Is there a better way to do this?
			store_sort.ResetDefaultSortFunc ();
			store_sort.DefaultSortFunc = 
				new Gtk.TreeIterCompareFunc (TaskSortFunc);
		}
	}
}