Namespace Global.Csls.Debugger.Fixtures.VisualBasic
    ''' <summary>
    ''' Provides a stable Visual Basic receiver for debugger function-evaluation tests.
    ''' </summary>
    Friend NotInheritable Class DebuggerFixtureValue
        ''' <summary>
        ''' Initializes the Visual Basic debugger receiver.
        ''' </summary>
        ''' <param name="number">The value returned by the debugger-visible method.</param>
        Friend Sub New(number As Integer)
            Me.Number = number
        End Sub

        ''' <summary>
        ''' Gets the value returned by the debugger-visible method.
        ''' </summary>
        Friend ReadOnly Property Number As Integer

        ''' <summary>
        ''' Computes a stable result by executing target code.
        ''' </summary>
        ''' <returns>The stored number incremented by one.</returns>
        Friend Function NextNumber() As Integer
            Return Number + 1
        End Function

        ''' <summary>
        ''' Adds one debugger-supplied argument to the stored number.
        ''' </summary>
        ''' <param name="value">The value supplied by managed function evaluation.</param>
        ''' <returns>The stored number plus the supplied value.</returns>
        Friend Function AddNumber(value As Integer) As Integer
            Return Number + value
        End Function

        ''' <summary>
        ''' Returns the length of a debugger-materialized string.
        ''' </summary>
        ''' <param name="value">The string supplied by managed function evaluation.</param>
        ''' <returns>The supplied string length.</returns>
        Friend Function StringLength(value As String) As Integer
            Return value.Length + Number - 41
        End Function
    End Class
End Namespace
