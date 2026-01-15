import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { ShellComponent } from './shared/components/shell/shell.component';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, ShellComponent],
  template: `
    <app-shell>
      <router-outlet />
    </app-shell>
  `,
  styles: `
    :host {
      display: block;
      height: 100vh;
    }
  `,
})
export class App {}
